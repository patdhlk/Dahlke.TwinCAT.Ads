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
/// <b>Torn-snapshot window.</b> Each operation snapshots the current underlying
/// connection once, then delegates to it. The pool may clear and dispose that
/// connection immediately after the snapshot is taken but before the delegated
/// call completes, in which case the in-flight operation runs against an
/// about-to-be-disposed connection. The pool's reconnect loop mitigates this
/// with a grace period before disposal; fully closing the window (waiting for a
/// live connection rather than throwing immediately) is deferred to a later
/// commit. This commit's contract is simply: no current connection at snapshot
/// time throws <see cref="AdsConnectionUnavailableException"/>.
/// </para>
/// </remarks>
internal sealed class AdsConnectionFacade : IAdsConnection
{
    private readonly string _plcId;
    private readonly PlcTargetOptions _options;

    // Read/written across the pool's loop thread and caller threads. All access
    // goes through Volatile/Interlocked so updates are visible without locking;
    // a plain field (not `volatile`) is used so Interlocked.CompareExchange can
    // take a ref to it without the CS0420 warning.
    private IManagedConnection? _current;

    public AdsConnectionFacade(string plcId, PlcTargetOptions options)
    {
        _plcId = plcId;
        _options = options;
    }

    /// <inheritdoc />
    public string PlcId => _plcId;

    /// <inheritdoc />
    public string DisplayName => _options.DisplayName;

    /// <inheritdoc />
    /// <remarks>
    /// Observational: <see langword="true"/> only when a current underlying
    /// connection exists and reports itself connected.
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
    /// connected connection in its registry.
    /// </summary>
    internal void SetCurrent(IManagedConnection connection) => Volatile.Write(ref _current, connection);

    /// <summary>
    /// Clears the facade's current connection, but only if it is still
    /// <paramref name="connection"/>. A compare-and-clear: if a newer connection
    /// has already replaced this one (e.g. via ForceReconnect), the newer pointer
    /// is left intact so a stale teardown can never blank a live connection.
    /// </summary>
    internal void ClearCurrent(IManagedConnection connection)
        => Interlocked.CompareExchange(ref _current, null, connection);

    /// <summary>
    /// Unconditionally clears the facade's current connection. Used on pool stop,
    /// where all connections are being torn down regardless of identity.
    /// </summary>
    internal void Clear() => Volatile.Write(ref _current, null);

    private IManagedConnection Snapshot()
        => Volatile.Read(ref _current) ?? throw new AdsConnectionUnavailableException(_plcId);

    /// <inheritdoc />
    public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct)
        => Snapshot().ReadValueAsync(symbolPath, ct);

    /// <inheritdoc />
    public Task WriteValueAsync(string symbolPath, object value, CancellationToken ct)
        => Snapshot().WriteValueAsync(symbolPath, value, ct);

    /// <inheritdoc />
    public Task<Dictionary<string, object?>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct)
        => Snapshot().ReadValuesAsync(symbolPaths, ct);

    /// <inheritdoc />
    public Task WriteValuesAsync(Dictionary<string, object> values, CancellationToken ct)
        => Snapshot().WriteValuesAsync(values, ct);

    /// <inheritdoc />
    public Task<AdsState> GetAdsStateAsync(CancellationToken ct)
        => Snapshot().GetAdsStateAsync(ct);

    /// <inheritdoc />
    public Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct)
        => Snapshot().SubscribeAsync(symbolPath, cycleTimeMs, callback, ct);
}
