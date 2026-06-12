namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Root options type for Dahlke.TwinCAT.Ads. Aggregates all subsystem
/// options and is bound from the application's configuration when
/// <c>ServiceCollectionExtensions.AddTwinCatAds</c> or
/// <c>ServiceCollectionExtensions.AddTwinCatAdsSimulation</c> is called.
/// </summary>
public sealed class TwinCatAdsOptions
{
    /// <summary>
    /// Named PLC targets, keyed by target identifier (case-insensitive).
    /// Populated from the <c>PlcTargets</c> configuration section.
    /// </summary>
    public Dictionary<string, PlcTargetOptions> Targets { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Embedded AMS router settings.
    /// Populated from the <c>AmsRouter</c> configuration section.
    /// </summary>
    public AmsRouterOptions Router { get; set; } = new();

    /// <summary>
    /// Diagnostics and observability settings.
    /// </summary>
    public AdsDiagnosticsOptions Diagnostics { get; set; } = new();
}
