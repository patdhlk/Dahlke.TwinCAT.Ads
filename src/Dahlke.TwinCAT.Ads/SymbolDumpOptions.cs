namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Options for the PLC symbol tree dump feature.
/// Populated from the <c>AdsSymbolDump</c> configuration section.
/// </summary>
public sealed class SymbolDumpOptions
{
    /// <summary>
    /// Whether symbol tree dumping is enabled.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum depth to traverse when walking the symbol tree.
    /// Defaults to <c>1</c>.
    /// </summary>
    public int MaxDepth { get; set; } = 1;

    /// <summary>
    /// Symbol name prefixes to include in the dump.
    /// When empty, all symbols up to <see cref="MaxDepth"/> are included.
    /// </summary>
    public List<string> Prefixes { get; set; } = new();
}
