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
/// </remarks>
internal sealed class AdsConnectionFacade : IAdsConnection
{
    private readonly string _plcId;
    private readonly PlcTargetOptions _options;
    private readonly TimeProvider _timeProvider;

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

    public AdsConnectionFacade(string plcId, PlcTargetOptions options, TimeProvider timeProvider)
    {
        _plcId = plcId;
        _options = options;
        _timeProvider = timeProvider;
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

    /// <summary>
    /// The current underlying connection the facade routes to, exposed for tests
    /// to assert routing/identity behaviour. <see langword="null"/> when the
    /// target has no live connection.
    /// </summary>
    internal IManagedConnection? CurrentForTesting => Volatile.Read(ref _current);

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
    public async Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct)
    {
        var conn = await SnapshotAsync(ct).ConfigureAwait(false);
        return await conn.SubscribeAsync(symbolPath, cycleTimeMs, callback, ct).ConfigureAwait(false);
    }
}
