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
/// signature-for-signature and inherit their documented semantics; the facade
/// forwards each one, so any drift between the two surfaces fails to compile
/// there. <see cref="SimulatedAdsConnection"/> implements BOTH interfaces — this
/// one for the pool, and <see cref="IAdsConnection"/> because handing a sim
/// directly to code that expects an <see cref="IAdsConnection"/> is a supported
/// testing pattern (pinned by the shared contract suite).
/// </para>
/// </remarks>
internal interface IManagedConnection : IDisposable
{
    // ---- Identity + operational surface (mirrors IAdsConnection) ---------

    string PlcId { get; }
    string DisplayName { get; }
    bool IsConnected { get; }

    Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct);
    Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct);
    Task<AdsValueResult> ReadValueWithMetadataAsync(string symbolPath, CancellationToken ct);
    Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct);
    Task WriteValueAsync(string symbolPath, object value, CancellationToken ct);
    Task<IReadOnlyDictionary<string, AdsValueResult>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct);
    Task<IReadOnlyDictionary<string, AdsValueResult>> WriteValuesAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct);
    Task<AdsRpcResult> InvokeRpcMethodAsync(string symbolPath, string methodName, object?[] parameters, CancellationToken ct);
    Task<IReadOnlyList<AdsEnumMember>> GetEnumMembersAsync(string typeName, CancellationToken ct);
    Task<AdsState> GetAdsStateAsync(CancellationToken ct);
    Task<AdsDeviceInfo> GetDeviceInfoAsync(CancellationToken ct);
    Task WriteControlAsync(AdsState state, ushort deviceState, CancellationToken ct);
    Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct);
    Task<IDisposable> SubscribeAsync<T>(string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct);
    Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<AdsNotification> callback, CancellationToken ct);
    Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, CancellationToken ct);
    Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, bool includeChildren, CancellationToken ct);
    Task<IReadOnlyList<AdsSymbolInfo>> SearchSymbolsAsync(string pattern, bool includeChildren, CancellationToken ct);

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
