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
}
