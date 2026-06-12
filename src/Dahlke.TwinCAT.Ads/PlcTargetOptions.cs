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
    /// Primarily intended for code-first configuration, where values keep their
    /// CLR types (e.g. <c>int</c>, <c>bool</c>). When populated from JSON/file
    /// configuration the values bind as <see cref="string"/>, so code that reads a
    /// seeded value should account for the string representation in that case.
    /// </remarks>
    public Dictionary<string, object?> InitialValues { get; set; } = new();
}
