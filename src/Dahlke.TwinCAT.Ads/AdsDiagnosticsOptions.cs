namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Diagnostics and observability options for Dahlke.TwinCAT.Ads.
/// </summary>
public sealed class AdsDiagnosticsOptions
{
    /// <summary>
    /// Settings for the optional PLC symbol tree dump feature.
    /// Populated from the <c>AdsSymbolDump</c> configuration section.
    /// The legacy <c>AdsSymbolTreeDump</c> root key is also bound for
    /// backward compatibility (new section wins when both are present).
    /// </summary>
    public SymbolDumpOptions SymbolDump { get; set; } = new();
}
