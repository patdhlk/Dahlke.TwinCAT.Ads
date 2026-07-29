namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Connection settings for a single PLC target.
/// </summary>
public sealed class PlcTargetOptions
{
    /// <summary>
    /// AMS Net ID of the target PLC, e.g. <c>192.168.1.10.1.1</c>.
    /// </summary>
    public string AmsNetId { get; set; } = string.Empty;

    /// <summary>
    /// ADS port of the target PLC runtime. Defaults to <c>851</c>
    /// (first TwinCAT 3 PLC runtime).
    /// </summary>
    public int Port { get; set; } = 851;

    /// <summary>
    /// Human-readable name for logs and dashboards.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Per-operation timeout in milliseconds. Defaults to <c>5000</c>.
    /// </summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Timeout in milliseconds for symbol-browsing operations
    /// (<see cref="IAdsConnection.GetSymbolsAsync(string?, bool, System.Threading.CancellationToken)"/> and
    /// <see cref="IAdsConnection.SearchSymbolsAsync"/>).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="TimeoutMs"/> because browsing uploads the PLC's symbol
    /// table, which takes far longer than reading a single value. Defaults to 30 seconds.
    /// </remarks>
    public int SymbolBrowseTimeoutMs { get; set; } = 30000;

    /// <summary>
    /// How this target connects: <see cref="ConnectionMode.Real"/> (over ADS/AMS,
    /// the default) or <see cref="ConnectionMode.Simulated"/> (in-memory store).
    /// </summary>
    /// <remarks>
    /// Binds from configuration as the enum member name, e.g.
    /// <c>PlcTargets:myPlc:Mode = "Simulated"</c>. Simulated targets need no
    /// AMS Net ID and never reach the network.
    /// </remarks>
    public ConnectionMode Mode { get; set; } = ConnectionMode.Real;

    /// <summary>
    /// Seed values applied to a <see cref="ConnectionMode.Simulated"/> target at
    /// creation, keyed by symbol path. Ignored for <see cref="ConnectionMode.Real"/>
    /// targets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In code-first configuration values keep their CLR types (e.g. <c>int</c>,
    /// <c>bool</c>) and are seeded verbatim.
    /// </para>
    /// <para>
    /// JSON/file configuration is string-typed, so an entry written as a bare scalar
    /// (<c>"MAIN.Speed": 1500</c>) is seeded as the <see cref="string"/> <c>"1500"</c> and a
    /// metadata read reports it as <c>STRING</c>. To seed a config value with the type a real
    /// PLC would report, declare it:
    /// <code>
    /// "InitialValues": {
    ///   "MAIN.Speed":   { "value": 1500, "type": "DINT"  },
    ///   "MAIN.Station": "Demo Station"
    /// }
    /// </code>
    /// <c>type</c> is an IEC 61131-3 elementary type name, matched case-insensitively with
    /// Beckhoff aliases resolved (see <see cref="Iec61131Converter.Beckhoff"/>). Omitting
    /// <c>value</c> seeds that type's default. A bad entry — unknown type, unconvertible
    /// value, or a <c>value</c> with no <c>type</c> — fails options validation at startup
    /// rather than being silently seeded as a string.
    /// </para>
    /// </remarks>
    public Dictionary<string, object?> InitialValues { get; set; } = new();

    /// <summary>
    /// Problems found while re-binding <see cref="InitialValues"/> from configuration,
    /// surfaced as options-validation failures by <see cref="TwinCatAdsOptionsValidator"/>.
    /// </summary>
    /// <remarks>
    /// Collected rather than thrown so the operator sees every bad seed entry in one startup
    /// failure. Internal: this is a channel between <see cref="InitialValueBinder"/> and the
    /// validator, not part of the configuration surface.
    /// </remarks>
    internal List<string> InitialValueBindingErrors { get; } = [];
}
