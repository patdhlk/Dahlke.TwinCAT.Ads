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
    /// Note: the symbol dump itself still reads the legacy key until the
    /// internal services migrate to these options; until then, changing
    /// these values has no runtime effect.
    /// </summary>
    public SymbolDumpOptions SymbolDump { get; set; } = new();
}
