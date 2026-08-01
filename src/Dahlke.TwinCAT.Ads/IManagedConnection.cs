using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Internal seam capturing everything the pool and facade need from an
/// underlying connection: the operational surface the facade forwards, plus the
/// lifecycle operations the pool's connection loop drives.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately NOT derived from <see cref="IAdsConnection"/>. The consumer
/// contract carries the connection-state surface — <see cref="IAdsConnection.State"/>
/// and <see cref="IAdsConnection.ConnectionStateChanged"/> — whose single owner is
/// the pool: it runs the connect/health/reconnect loop, decides the transitions,
/// and surfaces them through the <see cref="AdsConnectionFacade"/>. An underlying
/// connection structurally cannot raise those transitions, so deriving from the
/// consumer contract would force every adapter here to carry a state property
/// nothing reads and an event nothing can fire (as <see cref="AdsConnection"/>
/// did, behind a CS0067 pragma, until this seam was decoupled).
/// </para>
/// <para>
/// The operational members mirror their <see cref="IAdsConnection"/> counterparts
/// and inherit their documented semantics; the facade forwards each one, so any
/// drift between the two surfaces fails to compile there.
/// <see cref="SimulatedAdsConnection"/> implements BOTH interfaces — this one for
/// the pool, and <see cref="IAdsConnection"/> because handing a sim directly to
/// code that expects an <see cref="IAdsConnection"/> is a supported testing
/// pattern (pinned by the shared contract suite). Because the two signatures then
/// differ only by <c>timeout</c>, the sim implements THIS interface explicitly and
/// its public surface stays the consumer one.
/// </para>
/// <para>
/// <b>The one deliberate divergence: <c>timeout</c>.</b> Every operational member
/// takes a <see cref="TimeSpan"/>? the consumer contract does not, carrying the
/// per-call override behind <see cref="IAdsConnection.WithTimeout"/>. It cannot be
/// ambient state and it cannot be a field: the value is per CALL, while one
/// underlying connection serves every caller of a target concurrently. It cannot
/// live on a scoped CLONE of <see cref="AdsConnection"/> either — the enum-cache
/// generation guard is an <see cref="System.Threading.Interlocked"/>-mutated field,
/// which no second instance can share. So it is threaded explicitly, and
/// <see langword="null"/> means "use the target's configured bound".
/// </para>
/// </remarks>
internal interface IManagedConnection : IDisposable
{
    // ---- Identity + operational surface (mirrors IAdsConnection) ---------

    string PlcId { get; }
    string DisplayName { get; }
    bool IsConnected { get; }

    Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct, TimeSpan? timeout = null);
    Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct, TimeSpan? timeout = null);
    Task<AdsValueResult> ReadValueWithMetadataAsync(string symbolPath, CancellationToken ct, TimeSpan? timeout = null);
    Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct, TimeSpan? timeout = null);
    Task WriteValueAsync(string symbolPath, object value, CancellationToken ct, TimeSpan? timeout = null);
    Task<IReadOnlyDictionary<string, AdsValueResult>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct, TimeSpan? timeout = null);
    Task<IReadOnlyDictionary<string, AdsValueResult>> WriteValuesAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct, TimeSpan? timeout = null);
    Task<AdsRpcResult> InvokeRpcMethodAsync(string symbolPath, string methodName, object?[] parameters, CancellationToken ct, TimeSpan? timeout = null);
    Task<IReadOnlyList<AdsEnumMember>> GetEnumMembersAsync(string typeName, CancellationToken ct, TimeSpan? timeout = null);
    Task<AdsState> GetAdsStateAsync(CancellationToken ct, TimeSpan? timeout = null);
    Task<AdsDeviceInfo> GetDeviceInfoAsync(CancellationToken ct, TimeSpan? timeout = null);
    Task WriteControlAsync(AdsState state, ushort deviceState, CancellationToken ct, TimeSpan? timeout = null);
    Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct, TimeSpan? timeout = null);
    Task<IDisposable> SubscribeAsync<T>(string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct, TimeSpan? timeout = null);
    Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<AdsNotification> callback, CancellationToken ct, TimeSpan? timeout = null);
    Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, CancellationToken ct, TimeSpan? timeout = null);
    Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, bool includeChildren, CancellationToken ct, TimeSpan? timeout = null);
    Task<IReadOnlyList<AdsSymbolInfo>> SearchSymbolsAsync(string pattern, bool includeChildren, CancellationToken ct, TimeSpan? timeout = null);

    // ---- Lifecycle driven by the pool's connection loop ------------------

    void Connect();
    void Disconnect();
    Task<bool> IsAliveAsync(CancellationToken ct);
    void ForceDisconnect();

    /// <summary>
    /// Logs the PLC symbol tree for diagnostics.
    /// Only symbols whose depth and prefix match <paramref name="options"/> are emitted.
    /// </summary>
    void LogSymbolTree(SymbolDumpOptions options);
}
