using System.Collections.Concurrent;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// A stable, per-target <see cref="IAdsConnection"/> handed out by
/// <see cref="AdsConnectionPool"/>. Its identity never changes for the pool's
/// lifetime; every operation is routed to the current underlying
/// <see cref="IManagedConnection"/>, which the pool swaps in and out as it
/// connects, reconnects, and tears down.
/// </summary>
/// <remarks>
/// <para>
/// The facade follows a push model: it holds no reference back to the pool and
/// never reads the pool's connection registries. Instead the pool calls
/// <see cref="SetCurrent"/> at the moment it publishes a freshly connected
/// connection, and <see cref="ClearCurrent"/> (a compare-and-clear) at the
/// moment it removes one. This keeps construction acyclic — the pool creates
/// facades; facades never reach into the pool.
/// </para>
/// <para>
/// <b>Wait-then-throw.</b> Each operation snapshots the current underlying
/// connection via <see cref="SnapshotAsync"/>. When a connection is present the
/// snapshot returns it synchronously (fast path). When none is present — the
/// target has never connected, or is mid-outage awaiting reconnection — the
/// operation does NOT fail immediately. It waits up to the target's
/// <see cref="PlcTargetOptions.TimeoutMs"/> (measured against the pool's
/// <see cref="TimeProvider"/>) for a connection to be published. If a reconnect
/// lands inside that window the parked call proceeds against the new connection;
/// otherwise it throws <see cref="AdsConnectionUnavailableException"/> once the
/// window elapses. A caller's <see cref="CancellationToken"/> firing mid-wait
/// surfaces as an <see cref="OperationCanceledException"/> instead.
/// </para>
/// <para>
/// <b>Reusing <see cref="PlcTargetOptions.TimeoutMs"/>.</b> No new configuration
/// knob is introduced: <c>TimeoutMs</c> already promises "an operation may take
/// up to this long before failing", and the reconnect wait is exactly that — a
/// bounded delay before the operation either proceeds or fails. The default of
/// 5000ms therefore bounds how long an operation will block during an outage.
/// </para>
/// <para>
/// <b>Stopped vs transient outage.</b> A <see cref="Clear"/> issued while the
/// pool is merely reconnecting leaves the facade in a transient-outage state:
/// operations wait, because a connection may yet arrive. Once the pool is
/// stopping or disposing it calls <see cref="MarkStopped"/>, after which the
/// facade fails FAST — operations throw <see cref="AdsConnectionUnavailableException"/>
/// immediately (and wake any parked waiters) rather than burning the full
/// <c>TimeoutMs</c> waiting for a connection that will never come.
/// </para>
/// <para>
/// <b><see cref="IsConnected"/> is observational, not a guard.</b> It reports
/// whether a current underlying connection exists and is itself connected at the
/// instant it is read. It is NOT consulted by the operation methods and offers no
/// happens-before guarantee against a concurrent teardown; callers should treat
/// it as a hint, not a precondition, and let the operation's own wait-then-throw
/// contract govern correctness.
/// </para>
/// <para>
/// <b>Durable subscriptions.</b> Unlike one-shot operations, a subscription made
/// via <see cref="SubscribeAsync"/> outlives the connection it was first
/// registered on. The facade keeps every active subscription as a record (path,
/// cycle time, callback) in a registry. On the first call it registers
/// immediately against the current connection (waiting per the same wait-then-throw
/// contract); thereafter, whenever the pool publishes a new connection via
/// <see cref="SetCurrent"/>, the facade re-registers every active record against
/// it on a background task. The caller's returned <see cref="IDisposable"/> never
/// goes stale across reconnects: disposing it removes the record from the registry
/// (so it is not re-registered again) and disposes the current underlying
/// registration. Dispose is idempotent and thread-safe.
/// </para>
/// <para>
/// <b>Dispose-or-drop rule for replaced registrations.</b> Each record tracks the
/// underlying registration <i>together with the connection it was created on</i>.
/// When a re-registration completes, the facade stores it only if that connection
/// is STILL the facade's current connection AND the record is still active;
/// otherwise (a newer reconnect won the race, or the subscriber disposed the
/// handle mid-flight) the just-created registration is disposed immediately rather
/// than stored, so no <see cref="IDisposable"/> leaks. The facade never disposes a
/// registration belonging to a connection that has already been replaced — that
/// connection's <see cref="AdsClient"/> is torn down by the pool loop, so its
/// registrations die with it; reaching into a disposed client could throw. The
/// only registration the facade actively disposes is the record's <i>current</i>
/// one, and only on handle dispose (a live connection) or on the dispose-or-drop
/// path above.
/// </para>
/// </remarks>
internal sealed class AdsConnectionFacade : IAdsConnection
{
    private readonly string _plcId;
    private readonly PlcTargetOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    // Read/written across the pool's loop thread and caller threads. All access
    // goes through Volatile/Interlocked so updates are visible without locking;
    // a plain field (not `volatile`) is used so Interlocked.CompareExchange can
    // take a ref to it without the CS0420 warning.
    private IManagedConnection? _current;

    // Lazily armed by the first waiter when the fast path misses; shared by all
    // concurrent waiters. SetCurrent completes it (handing every waiter the newly
    // published connection); MarkStopped faults it. Always swapped out with
    // Interlocked.Exchange so exactly one publisher/stopper resolves it.
    // Created with RunContinuationsAsynchronously so a completing publisher (the
    // pool's loop thread) never runs waiter continuations inline.
    private TaskCompletionSource<IManagedConnection>? _waiters;

    // Set once, by MarkStopped, on pool stop/dispose. A stopped facade fails fast.
    private volatile bool _stopped;

    // Current state, written only by OnStateChanged (called from the pool's loop
    // thread via SetState). Volatile so reads from any thread observe the latest
    // value without a lock.
    private volatile ConnectionState _state = ConnectionState.Disconnected;

    // Active durable subscriptions. A concurrent set (value byte is a dummy):
    // SubscribeAsync adds, handle dispose removes, SetCurrent enumerates to
    // re-register. Enumeration of a ConcurrentDictionary is safe under concurrent
    // mutation, which is exactly what re-registration-vs-subscribe/dispose needs.
    private readonly ConcurrentDictionary<DurableSubscription, byte> _subscriptions = new();

    // The most recent background re-registration task, tracked (not async void) so
    // failures surface and tests can reason about lifecycle. Each SetCurrent
    // overwrites it; a later reconnect's task supersedes an earlier one. We never
    // await it on the loop thread — SetCurrent must stay synchronous — but holding
    // the reference keeps the Task rooted and lets us chain/observe if needed.
    private Task _reRegisterTask = Task.CompletedTask;

    public AdsConnectionFacade(string plcId, PlcTargetOptions options, TimeProvider timeProvider, ILogger logger)
    {
        _plcId = plcId;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string PlcId => _plcId;

    /// <inheritdoc />
    public string DisplayName => _options.DisplayName;

    /// <inheritdoc />
    /// <remarks>
    /// Observational only: <see langword="true"/> when a current underlying
    /// connection exists and reports itself connected. It is NOT a guard — the
    /// operation methods never consult it; they wait-then-throw on their own.
    /// </remarks>
    public bool IsConnected => Volatile.Read(ref _current) is { IsConnected: true };

    /// <inheritdoc />
    /// <remarks>
    /// Observational snapshot: reflects the state most recently forwarded by the
    /// pool's <c>SetState</c> helper. Safe to read from any thread; the field is
    /// <c>volatile</c> so no lock is needed.
    /// </remarks>
    public ConnectionState State => _state;

    /// <inheritdoc />
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// The current underlying connection the facade routes to, exposed for tests
    /// to assert routing/identity behaviour. <see langword="null"/> when the
    /// target has no live connection.
    /// </summary>
    internal IManagedConnection? CurrentForTesting => Volatile.Read(ref _current);

    /// <summary>
    /// Called by <see cref="AdsConnectionPool"/>'s <c>SetState</c> helper immediately
    /// after it records the new state and raises its own internal event. Stores the
    /// new state and raises this facade's public <see cref="ConnectionStateChanged"/>
    /// event, catching and logging any exception thrown by a handler so a faulty
    /// subscriber can never tear down the pool loop.
    /// </summary>
    /// <remarks>
    /// The pool calls this only when the state has actually changed (same
    /// change-guard as its own event), so this method can assume
    /// <paramref name="args"/>.State != <paramref name="args"/>.PreviousState.
    /// </remarks>
    internal void OnStateChanged(ConnectionStateChangedEventArgs args)
    {
        // Store BEFORE raising — a handler that reads State sees the new value.
        _state = args.State;

        var handlers = ConnectionStateChanged;
        if (handlers is null)
            return;

        // Invoke each handler individually so one throwing handler does not skip
        // the rest. The standard multicast delegate would abort the chain on the
        // first exception; we replicate its invocation list instead.
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<ConnectionStateChangedEventArgs>)handler)(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "ConnectionStateChanged handler threw while reporting {PlcId} -> {State}",
                    _plcId,
                    args.State);
            }
        }
    }

    /// <summary>
    /// Publishes <paramref name="connection"/> as the facade's current underlying
    /// connection. Called by the pool immediately after it stores a freshly
    /// connected connection in its registry. Any operations parked in
    /// <see cref="SnapshotAsync"/> are released and proceed against
    /// <paramref name="connection"/>.
    /// </summary>
    internal void SetCurrent(IManagedConnection connection)
    {
        // Order matters for the lost-wakeup guarantee: publish _current FIRST so a
        // waiter that arms its TCS after this point (but before we resolve it)
        // observes the connection on its post-arm re-read and returns without
        // awaiting. THEN hand the connection to any already-armed waiters.
        Volatile.Write(ref _current, connection);

        // A loop wedged in a synchronous Connect() past StopAsync's teardown
        // timeout can publish AFTER MarkStopped — roll back so a stopped facade
        // is never resurrected to route at a connection the pool is disposing.
        if (_stopped)
        {
            Volatile.Write(ref _current, null);
            return;
        }

        Interlocked.Exchange(ref _waiters, null)?.TrySetResult(connection);

        // Re-register every active durable subscription against the freshly
        // published connection. SetCurrent is synchronous and runs on the pool's
        // loop thread; the underlying SubscribeAsync is async, so we fire the
        // re-registrations as a TRACKED background task (never async void) instead
        // of blocking the loop. A failed re-registration is logged and the record
        // retained so the NEXT reconnect retries it.
        if (!_subscriptions.IsEmpty)
            _reRegisterTask = ReRegisterAllAsync(connection);
    }

    /// <summary>
    /// Re-registers every currently active durable subscription against
    /// <paramref name="connection"/>. Runs as a background task fired by
    /// <see cref="SetCurrent"/>; per-record failures are isolated and logged at
    /// Warning, leaving the record in the registry to retry on the next reconnect.
    /// </summary>
    private async Task ReRegisterAllAsync(IManagedConnection connection)
    {
        // Snapshot the keys so a concurrent dispose/add does not disturb the loop.
        // A record removed after the snapshot is handled inside RegisterAsync via
        // the dispose-or-drop check, so the worst case is a wasted registration
        // that we immediately dispose.
        foreach (var subscription in _subscriptions.Keys)
        {
            try
            {
                await subscription.RegisterAsync(connection, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to re-register subscription for {Symbol} on {PlcId} after reconnect; will retry on next reconnect.",
                    subscription.SymbolPath,
                    _plcId);
            }
        }
    }

    /// <summary>
    /// Clears the facade's current connection, but only if it is still
    /// <paramref name="connection"/>. A compare-and-clear: if a newer connection
    /// has already replaced this one (e.g. via ForceReconnect), the newer pointer
    /// is left intact so a stale teardown can never blank a live connection.
    /// </summary>
    /// <remarks>
    /// This is a TRANSIENT clear: it does not mark the facade stopped, so
    /// subsequent operations wait (a reconnect may yet publish a connection)
    /// rather than fail fast.
    /// </remarks>
    internal void ClearCurrent(IManagedConnection connection)
        => Interlocked.CompareExchange(ref _current, null, connection);

    /// <summary>
    /// Unconditionally clears the facade's current connection. Used on pool stop,
    /// where all connections are being torn down regardless of identity.
    /// </summary>
    /// <remarks>
    /// Like <see cref="ClearCurrent"/> this is a TRANSIENT clear and does not
    /// mark the facade stopped; <see cref="MarkStopped"/> governs fast-fail.
    /// </remarks>
    internal void Clear() => Volatile.Write(ref _current, null);

    /// <summary>
    /// Marks the facade permanently stopped (pool StopAsync/Dispose). After this,
    /// <see cref="SnapshotAsync"/> fails fast with
    /// <see cref="AdsConnectionUnavailableException"/> instead of waiting out
    /// <see cref="PlcTargetOptions.TimeoutMs"/>, and any already-parked waiters are
    /// woken with the same exception.
    /// </summary>
    internal void MarkStopped()
    {
        _stopped = true;
        // A stopped facade is by definition disconnected — don't leave a stale
        // Connected reading in the window before the pool's final SetState sweep.
        _state = ConnectionState.Disconnected;
        // Drop the current pointer: a stopped facade must never report connected
        // nor route to a connection the pool is about to dispose.
        Volatile.Write(ref _current, null);
        // Wake anyone already parked: a connection will never come now.
        Interlocked.Exchange(ref _waiters, null)?.TrySetException(StoppedException());
    }

    /// <summary>
    /// Returns the current underlying connection, waiting up to
    /// <see cref="PlcTargetOptions.TimeoutMs"/> for one to be published when none
    /// is present. Fast path (a connection is already current) completes
    /// synchronously. Throws <see cref="AdsConnectionUnavailableException"/> on
    /// timeout or when the facade is stopped, or <see cref="OperationCanceledException"/>
    /// if <paramref name="ct"/> fires first.
    /// </summary>
    private ValueTask<IManagedConnection> SnapshotAsync(CancellationToken ct)
    {
        // Fast path: a connection is already current — return it without allocating
        // a Task. A stopped facade short-circuits to a fast fail-fast throw.
        var current = Volatile.Read(ref _current);
        if (current is not null)
            return new ValueTask<IManagedConnection>(current);

        if (_stopped)
            return ValueTask.FromException<IManagedConnection>(StoppedException());

        return new ValueTask<IManagedConnection>(WaitForConnectionAsync(ct));
    }

    private async Task<IManagedConnection> WaitForConnectionAsync(CancellationToken ct)
    {
        // Arm (or join) the shared waiter TCS. CompareExchange installs ours only
        // if the slot is empty; otherwise an existing waiter's TCS is reused, so
        // every concurrent waiter shares one completion source.
        var tcs = new TaskCompletionSource<IManagedConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = Interlocked.CompareExchange(ref _waiters, tcs, null) ?? tcs;

        // Re-check AFTER arming. This closes the lost-wakeup window: SetCurrent
        // writes _current before it resolves _waiters, so if a publish slipped in
        // between the fast-path miss and arming above, we observe it here and
        // return immediately rather than awaiting a TCS no one will complete.
        // Likewise re-check the stopped flag (MarkStopped sets it before faulting
        // the TCS) so a stop racing the arm is never missed.
        var current = Volatile.Read(ref _current);
        if (current is not null)
            return current;
        if (_stopped)
            throw StoppedException();

        try
        {
            return await shared.Task
                .WaitAsync(TimeSpan.FromMilliseconds(_options.TimeoutMs), _timeProvider, ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new AdsConnectionUnavailableException(_plcId);
        }
    }

    private AdsConnectionUnavailableException StoppedException()
        => new(
            _plcId,
            $"PLC target '{_plcId}' is unavailable — the connection pool has been stopped.",
            innerException: null);

    /// <inheritdoc />
    public async Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct)
    {
        var conn = await SnapshotAsync(ct).ConfigureAwait(false);
        return await conn.ReadValueAsync(symbolPath, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteValueAsync(string symbolPath, object value, CancellationToken ct)
    {
        var conn = await SnapshotAsync(ct).ConfigureAwait(false);
        await conn.WriteValueAsync(symbolPath, value, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object?>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct)
    {
        var conn = await SnapshotAsync(ct).ConfigureAwait(false);
        return await conn.ReadValuesAsync(symbolPaths, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteValuesAsync(Dictionary<string, object> values, CancellationToken ct)
    {
        var conn = await SnapshotAsync(ct).ConfigureAwait(false);
        await conn.WriteValuesAsync(values, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AdsState> GetAdsStateAsync(CancellationToken ct)
    {
        var conn = await SnapshotAsync(ct).ConfigureAwait(false);
        return await conn.GetAdsStateAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Registers a DURABLE subscription. The record (path, cycle time, callback) is
    /// added to the facade's registry and registered immediately against the current
    /// connection (waiting per the wait-then-throw contract during an outage). The
    /// returned <see cref="IDisposable"/> survives reconnects: the facade
    /// re-registers the record on each newly published connection, and the same
    /// handle keeps removing the subscription permanently when disposed.
    /// </remarks>
    public async Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct)
    {
        var subscription = new DurableSubscription(this, symbolPath, cycleTimeMs, callback);

        // Add to the registry BEFORE the first registration. If a reconnect fires
        // between here and the immediate registration below, the background
        // re-registration sees the record (idempotency is fine: RegisterAsync
        // replaces the current registration atomically). If we registered first and
        // added second, a reconnect in that gap would miss the record entirely.
        _subscriptions[subscription] = 0;

        try
        {
            var conn = await SnapshotAsync(ct).ConfigureAwait(false);
            await subscription.RegisterAsync(conn, ct).ConfigureAwait(false);
        }
        catch
        {
            // The initial registration failed (outage timed out, cancelled, or the
            // underlying threw). Roll back so a never-registered subscription does
            // not linger in the registry; the caller gets the exception and no
            // handle. Dispose is idempotent, so this is safe even if a racing
            // reconnect already touched the record.
            subscription.Dispose();
            throw;
        }

        return subscription;
    }

    /// <summary>
    /// A durable subscription record: the immutable subscribe arguments plus the
    /// current underlying registration (and the connection it was created on). Held
    /// in the facade's registry and re-registered on every reconnect. Disposing the
    /// handle removes it from the registry and disposes the live registration.
    /// </summary>
    private sealed class DurableSubscription : IDisposable
    {
        private readonly AdsConnectionFacade _facade;
        private readonly object _gate = new();

        // The connection the current registration was created on, and that
        // registration. Guarded by _gate so RegisterAsync's compare-store and
        // Dispose's read-clear never interleave. _disposed flips once, under the
        // same gate, so a registration completing after dispose is dropped.
        //
        // _registeredOn doubles as a RESERVATION: it is set to a connection BEFORE
        // the underlying SubscribeAsync await begins, so a second concurrent
        // RegisterAsync for the same connection (e.g. a parked initial subscribe
        // racing the SetCurrent-fired re-registration) observes it and skips,
        // preventing a duplicate underlying registration. _registration trails it:
        // it is null between reservation and the await completing.
        private IManagedConnection? _registeredOn;
        private IDisposable? _registration;
        private bool _disposed;

        public DurableSubscription(
            AdsConnectionFacade facade, string symbolPath, int cycleTimeMs, Action<string, object?> callback)
        {
            _facade = facade;
            SymbolPath = symbolPath;
            CycleTimeMs = cycleTimeMs;
            Callback = callback;
        }

        public string SymbolPath { get; }
        public int CycleTimeMs { get; }
        public Action<string, object?> Callback { get; }

        /// <summary>
        /// Registers this subscription against <paramref name="connection"/> and
        /// stores the resulting registration as the record's current one — but only
        /// if, after the async registration completes, this record is still active
        /// AND <paramref name="connection"/> is still the facade's current
        /// connection. Otherwise the just-created registration is disposed (the
        /// dispose-or-drop rule): the subscriber disposed mid-flight, or a newer
        /// reconnect already won. Any previous registration is left untouched — it
        /// belonged to a now-dead connection and is torn down by the pool loop.
        /// </summary>
        public async Task RegisterAsync(IManagedConnection connection, CancellationToken ct)
        {
            // Reserve this connection under the gate BEFORE awaiting the underlying
            // SubscribeAsync. Skip if already disposed, or if we have already
            // reserved/registered against this very connection — that de-dupes the
            // case where SetCurrent both releases a parked initial subscribe AND
            // fires the background re-registration for the same freshly published
            // connection. The reservation drops the previous registration reference
            // (a prior dead connection's, torn down by the pool loop — see the
            // dispose-or-drop rule) without disposing it.
            lock (_gate)
            {
                if (_disposed)
                    return;
                if (ReferenceEquals(_registeredOn, connection))
                    return;
                _registeredOn = connection;
                _registration = null;
            }

            IDisposable fresh;
            try
            {
                fresh = await connection.SubscribeAsync(SymbolPath, CycleTimeMs, Callback, ct).ConfigureAwait(false);
            }
            catch
            {
                // The underlying registration failed. Release our reservation so the
                // next reconnect retries — but only if it is still ours (a newer
                // reconnect may already have re-reserved a different connection).
                lock (_gate)
                {
                    if (ReferenceEquals(_registeredOn, connection))
                        _registeredOn = null;
                }
                throw;
            }

            IDisposable? toDispose = null;
            lock (_gate)
            {
                // Store the live registration ONLY if we are still active, still
                // hold the reservation for this connection, and this connection is
                // still the one the facade routes to. Otherwise (disposed mid-flight,
                // or a later SetCurrent re-reserved/won) drop what we just created.
                if (!_disposed
                    && ReferenceEquals(_registeredOn, connection)
                    && ReferenceEquals(Volatile.Read(ref _facade._current), connection))
                {
                    _registration = fresh;
                }
                else
                {
                    toDispose = fresh;
                }
            }

            toDispose?.Dispose();
        }

        /// <summary>
        /// Removes the record from the facade registry and disposes the current
        /// underlying registration (if any). Idempotent and thread-safe via the
        /// gate + first-disposer guard.
        /// </summary>
        public void Dispose()
        {
            IDisposable? registration;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                registration = _registration;
                _registration = null;
                _registeredOn = null;
            }

            // Remove from the registry so no future reconnect re-registers it.
            _facade._subscriptions.TryRemove(this, out _);

            // Dispose the live registration. It belongs to whatever connection was
            // current when it was created; the only registration we ever hold here
            // is the most recent successful one. If that connection has since died,
            // its registration disposable is a harmless no-op (the underlying
            // notification is already gone); if it is live, this removes the
            // notification cleanly.
            registration?.Dispose();
        }
    }
}
