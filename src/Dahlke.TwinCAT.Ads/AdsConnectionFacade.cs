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
/// <b>Stopped vs transient outage.</b> A <see cref="ClearCurrent"/> issued while
/// the pool is merely reconnecting leaves the facade in a transient-outage state:
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
/// via any <c>SubscribeAsync</c> overload outlives the connection it was first
/// registered on. The mechanics — the record registry, publish-before-first-
/// registration, reserve/commit-or-hand-back, restore-on-swap, and the disposal
/// guarantee — live in
/// <see cref="DurableSubscriptionRegistry{TTarget, THandle, TMeta}"/>, of which
/// this facade is one of two adapters (the raw channel is the other). Each
/// record stores a REGISTRAR delegate already bound to its overload's callback,
/// so every callback shape re-registers through one path. On the first call the
/// subscription registers against the current connection (waiting per the same
/// wait-then-throw contract); thereafter each <see cref="SetCurrent"/> restores
/// every active record against the new connection on a background task. The
/// caller's <see cref="IDisposable"/> never goes stale across reconnects, and
/// its dispose is idempotent and thread-safe. This facade's one adapter-specific
/// rule: a registration commits only while its connection is STILL the facade's
/// current one (the registry's commit guard), so a registration created for an
/// already-replaced connection is disposed rather than stored.
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

    // Active durable subscriptions — owned by the shared registry module, which
    // holds every durable-subscription invariant (publish-before-first-
    // registration, reserve/commit-or-hand-back, restore-on-swap, the delivery
    // guarantee). This facade is one of its two adapters; AdsRawChannel is the
    // other. Adapter-specific policy is injected in the constructor: a
    // registration is an IDisposable and discarding it is disposing it, and the
    // commit guard is the facade's current-pointer check — a registration only
    // commits while its connection is still the one this facade routes to.
    private readonly DurableSubscriptionRegistry<IManagedConnection, IDisposable, string> _subscriptions;

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
        _subscriptions = new DurableSubscriptionRegistry<IManagedConnection, IDisposable, string>(
            discard: (_, registration) => registration.Dispose(),
            commitGuard: connection => ReferenceEquals(Volatile.Read(ref _current), connection),
            onRestoreFailure: (symbolPath, ex) => _logger.LogWarning(
                ex,
                "Failed to re-register subscription for {Symbol} on {PlcId} after reconnect; will retry on next reconnect.",
                symbolPath,
                _plcId));
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
    /// The current underlying connection the facade routes to.
    /// <see langword="null"/> when the target has no live connection. Backs
    /// <see cref="IAdsConnectionPool.TryGetSimulatedConnection"/> — which
    /// type-tests it for a live <see cref="SimulatedAdsConnection"/> — and lets
    /// tests assert routing/identity behaviour.
    /// </summary>
    internal IManagedConnection? Current => Volatile.Read(ref _current);

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
        // restore pass as a TRACKED background task (never async void) instead
        // of blocking the loop. Per-record isolation, retain-on-failure, and the
        // no-caller-token rule live in the registry's RestoreAllAsync.
        if (!_subscriptions.IsEmpty)
            _reRegisterTask = _subscriptions.RestoreAllAsync(connection);
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
    private ValueTask<IManagedConnection> SnapshotAsync(CancellationToken ct, TimeSpan? timeout)
    {
        // Fast path: a connection is already current — return it without allocating
        // a Task. A stopped facade short-circuits to a fast fail-fast throw.
        var current = Volatile.Read(ref _current);
        if (current is not null)
            return new ValueTask<IManagedConnection>(current);

        if (_stopped)
            return ValueTask.FromException<IManagedConnection>(StoppedException());

        return new ValueTask<IManagedConnection>(WaitForConnectionAsync(ct, timeout));
    }

    private async Task<IManagedConnection> WaitForConnectionAsync(CancellationToken ct, TimeSpan? timeout)
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
                .WaitAsync(timeout ?? TimeSpan.FromMilliseconds(_options.TimeoutMs), _timeProvider, ct)
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
    public Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct)
        => ReadValueAsync<T>(symbolPath, ct, null);

    internal async Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct, TimeSpan? timeout)
    {
        var conn = await SnapshotAsync(ct, timeout).ConfigureAwait(false);
        return await conn.ReadValueAsync<T>(symbolPath, ct, timeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct)
        => ReadValueAsync(symbolPath, ct, null);

    internal async Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct, TimeSpan? timeout)
    {
        var conn = await SnapshotAsync(ct, timeout).ConfigureAwait(false);
        return await conn.ReadValueAsync(symbolPath, ct, timeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<AdsValueResult> ReadValueWithMetadataAsync(string symbolPath, CancellationToken ct)
        => ReadValueWithMetadataAsync(symbolPath, ct, null);

    internal async Task<AdsValueResult> ReadValueWithMetadataAsync(string symbolPath, CancellationToken ct, TimeSpan? timeout)
    {
        var conn = await SnapshotAsync(ct, timeout).ConfigureAwait(false);
        return await conn.ReadValueWithMetadataAsync(symbolPath, ct, timeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct)
        => WriteValueAsync<T>(symbolPath, value, ct, null);

    internal async Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct, TimeSpan? timeout)
    {
        var conn = await SnapshotAsync(ct, timeout).ConfigureAwait(false);
        await conn.WriteValueAsync<T>(symbolPath, value, ct, timeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task WriteValueAsync(string symbolPath, object value, CancellationToken ct)
        => WriteValueAsync(symbolPath, value, ct, null);

    internal async Task WriteValueAsync(string symbolPath, object value, CancellationToken ct, TimeSpan? timeout)
    {
        var conn = await SnapshotAsync(ct, timeout).ConfigureAwait(false);
        await conn.WriteValueAsync(symbolPath, value, ct, timeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Snapshot-once.</b> The whole batch runs against ONE connection captured by a single
    /// <c>SnapshotAsync</c> at the start. If a reconnect happens mid-batch, the batch still
    /// completes against the originally captured connection. During an outage this waits up to
    /// <see cref="PlcTargetOptions.TimeoutMs"/> then throws
    /// <see cref="AdsConnectionUnavailableException"/> for the whole batch.
    /// </remarks>
    public Task<IReadOnlyDictionary<string, AdsValueResult>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct)
        => ReadValuesAsync(symbolPaths, ct, null);

    internal async Task<IReadOnlyDictionary<string, AdsValueResult>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct, TimeSpan? timeout)
    {
        var conn = await SnapshotAsync(ct, timeout).ConfigureAwait(false);
        return await conn.ReadValuesAsync(symbolPaths, ct, timeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Snapshot-once: see <see cref="ReadValuesAsync(IEnumerable{string}, CancellationToken)"/>. The whole batch runs against one
    /// captured connection.
    /// </remarks>
    public Task<IReadOnlyDictionary<string, AdsValueResult>> WriteValuesAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct)
        => WriteValuesAsync(values, ct, null);

    internal async Task<IReadOnlyDictionary<string, AdsValueResult>> WriteValuesAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct, TimeSpan? timeout)
    {
        var conn = await SnapshotAsync(ct, timeout).ConfigureAwait(false);
        return await conn.WriteValuesAsync(values, ct, timeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<AdsRpcResult> InvokeRpcMethodAsync(
        string symbolPath, string methodName, object?[] parameters, CancellationToken ct)
        => InvokeRpcMethodAsync(symbolPath, methodName, parameters, ct, null);

    internal async Task<AdsRpcResult> InvokeRpcMethodAsync(
        string symbolPath, string methodName, object?[] parameters, CancellationToken ct, TimeSpan? timeout)
    {
        var conn = await SnapshotAsync(ct, timeout).ConfigureAwait(false);
        return await conn.InvokeRpcMethodAsync(symbolPath, methodName, parameters, ct, timeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AdsEnumMember>> GetEnumMembersAsync(string typeName, CancellationToken ct)
        => GetEnumMembersAsync(typeName, ct, null);

    internal async Task<IReadOnlyList<AdsEnumMember>> GetEnumMembersAsync(string typeName, CancellationToken ct, TimeSpan? timeout)
    {
        var conn = await SnapshotAsync(ct, timeout).ConfigureAwait(false);
        return await conn.GetEnumMembersAsync(typeName, ct, timeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<AdsState> GetAdsStateAsync(CancellationToken ct)
        => GetAdsStateAsync(ct, null);

    internal async Task<AdsState> GetAdsStateAsync(CancellationToken ct, TimeSpan? timeout)
    {
        var conn = await SnapshotAsync(ct, timeout).ConfigureAwait(false);
        return await conn.GetAdsStateAsync(ct, timeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<AdsDeviceInfo> GetDeviceInfoAsync(CancellationToken ct)
        => GetDeviceInfoAsync(ct, null);

    internal async Task<AdsDeviceInfo> GetDeviceInfoAsync(CancellationToken ct, TimeSpan? timeout)
    {
        var conn = await SnapshotAsync(ct, timeout).ConfigureAwait(false);
        return await conn.GetDeviceInfoAsync(ct, timeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task WriteControlAsync(AdsState state, ushort deviceState, CancellationToken ct)
        => WriteControlAsync(state, deviceState, ct, null);

    internal async Task WriteControlAsync(AdsState state, ushort deviceState, CancellationToken ct, TimeSpan? timeout)
    {
        var conn = await SnapshotAsync(ct, timeout).ConfigureAwait(false);
        await conn.WriteControlAsync(state, deviceState, ct, timeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Registers a DURABLE subscription. The record (path plus a registrar that binds
    /// the cycle time and this callback) is added to the facade's registry and
    /// registered immediately against the current connection (waiting per the
    /// wait-then-throw contract during an outage). The returned
    /// <see cref="IDisposable"/> survives reconnects: the facade re-registers the
    /// record on each newly published connection, and the same handle keeps removing
    /// the subscription permanently when disposed.
    /// </remarks>
    public Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct)
        => SubscribeAsync(symbolPath, cycleTimeMs, callback, ct, null);

    internal Task<IDisposable> SubscribeAsync(
        string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct, TimeSpan? timeout)
        => SubscribeCoreAsync(
            symbolPath,
            (conn, token) => conn.SubscribeAsync(symbolPath, cycleTimeMs, callback, token, timeout),
            timeout,
            ct);

    /// <inheritdoc />
    /// <remarks>
    /// Durable in exactly the same way as
    /// <see cref="SubscribeAsync(string, int, Action{string, object?}, CancellationToken)"/>, and
    /// through exactly the same code: the only difference is which underlying overload this
    /// overload's registrar closes over. The record itself stores no callback, so
    /// the registry's restore and register paths never branch on callback shape.
    /// </remarks>
    public Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<AdsNotification> callback, CancellationToken ct)
        => SubscribeAsync(symbolPath, cycleTimeMs, callback, ct, null);

    internal Task<IDisposable> SubscribeAsync(
        string symbolPath, int cycleTimeMs, Action<AdsNotification> callback, CancellationToken ct, TimeSpan? timeout)
        => SubscribeCoreAsync(
            symbolPath,
            (conn, token) => conn.SubscribeAsync(symbolPath, cycleTimeMs, callback, token, timeout),
            timeout,
            ct);

    /// <summary>
    /// The one durable-subscription path, shared by every <c>SubscribeAsync</c> overload:
    /// creates the record, publishes it to the registry, and performs the first registration.
    /// <paramref name="register"/> is the record's registrar — it has already captured the cycle
    /// time and the caller's callback (in whatever shape that callback has), so everything
    /// downstream of here, including re-registration after a reconnect, is callback-agnostic.
    /// </summary>
    private Task<IDisposable> SubscribeCoreAsync(
        string symbolPath,
        Func<IManagedConnection, CancellationToken, Task<IDisposable>> register,
        TimeSpan? timeout,
        CancellationToken ct)
        // AddAsync publishes the record BEFORE the initial registration below
        // runs, and rolls it back if that registration fails — see the registry's
        // remarks for why the ordering matters and why the rollback cannot race a
        // concurrent reconnect into a leak. The initial registration itself is
        // this facade's: acquire the current connection per the wait-then-throw
        // contract, then register through the shared reserve/commit path.
        => _subscriptions.AddAsync(
            symbolPath,
            (_, conn, token) => register(conn, token),
            initialRegister: async record =>
            {
                var conn = await SnapshotAsync(ct, timeout).ConfigureAwait(false);
                await _subscriptions.RegisterAsync(record, conn, ct).ConfigureAwait(false);
            });

    /// <inheritdoc />
    /// <remarks>
    /// The typed callback is wrapped FIRST with <see cref="TypedCallbackAdapter.Wrap{T}"/>
    /// into the untyped <c>Action&lt;string, object?&gt;</c> shape, then handed to the
    /// durable untyped <see cref="SubscribeAsync(string, int, Action{string, object?}, CancellationToken)"/>.
    /// Durability comes for free: the durable record stores the already-wrapped untyped
    /// callback, so each reconnect re-registers the same wrapper (conversion included)
    /// without the facade needing to know the subscription was typed.
    /// </remarks>
    public Task<IDisposable> SubscribeAsync<T>(string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct)
        => SubscribeAsync(symbolPath, cycleTimeMs, callback, ct, null);

    internal Task<IDisposable> SubscribeAsync<T>(
        string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct, TimeSpan? timeout)
        => SubscribeAsync(symbolPath, cycleTimeMs, TypedCallbackAdapter.Wrap(callback, _logger), ct, timeout);

    /// <inheritdoc />
    public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, CancellationToken ct)
        => GetSymbolsAsync(parentPath, ct, null);

    internal async Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, CancellationToken ct, TimeSpan? timeout)
    {
        var conn = await SnapshotAsync(ct, timeout).ConfigureAwait(false);
        return await conn.GetSymbolsAsync(parentPath, ct, timeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, bool includeChildren, CancellationToken ct)
        => GetSymbolsAsync(parentPath, includeChildren, ct, null);

    internal async Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, bool includeChildren, CancellationToken ct, TimeSpan? timeout)
    {
        var conn = await SnapshotAsync(ct, timeout).ConfigureAwait(false);
        return await conn.GetSymbolsAsync(parentPath, includeChildren, ct, timeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AdsSymbolInfo>> SearchSymbolsAsync(string pattern, bool includeChildren, CancellationToken ct)
        => SearchSymbolsAsync(pattern, includeChildren, ct, null);

    internal async Task<IReadOnlyList<AdsSymbolInfo>> SearchSymbolsAsync(string pattern, bool includeChildren, CancellationToken ct, TimeSpan? timeout)
    {
        var conn = await SnapshotAsync(ct, timeout).ConfigureAwait(false);
        return await conn.SearchSymbolsAsync(pattern, includeChildren, ct, timeout).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IAdsConnection WithTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        return new TimeoutScopedConnection(this, timeout);
    }

}
