namespace Dahlke.EtherCAT.Esi;

/// <summary>
/// The ESI catalog's configuration. App-level rather than per-PLC — an ESI set is a machine-wide
/// vendor library, identical no matter which PLC is being interrogated, so putting it on the
/// per-PLC <c>EtherCatOptions</c> would make every target repeat the same path.
/// </summary>
public sealed class EsiOptions
{
    /// <summary>The configuration section this options type binds from.</summary>
    public const string SectionName = "EtherCat:Esi";

    /// <summary>
    /// Directory of vendor ESI XML files. Null, blank, or non-existent turns ESI enrichment off
    /// entirely: no additional reads happen, and every PRE-EXISTING field on every EtherCAT
    /// response stays exactly as it was. That guarantee does not extend to slave detail's own
    /// <c>esi</c>/<c>esiStatus</c> fields — those are new regardless of configuration, always
    /// present, reporting <c>esi: null</c> and <c>esiStatus: "notConfigured"</c>.
    ///
    /// <para>
    /// Files are matched with the glob <c>"*.xml"</c>, which is case-SENSITIVE on Linux. An ESI
    /// set shipped with <c>.XML</c> extensions (uppercase, as some vendor archives ship them) is
    /// silently invisible there: the directory exists and is configured, so every lookup resolves
    /// to a clean <c>notFound</c> rather than any error naming the mismatch.
    /// </para>
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>
    /// Wall-clock bound on ONE device's cold resolve, checked between candidate files. A resolve
    /// that exceeds it stops and reports the device as not found, logging a Warning that names
    /// this option.
    ///
    /// <para>
    /// An absolute value rather than a per-file one, because the question is "how long can a
    /// request stall on a cold lookup", not "how many files". The reference Beckhoff set is
    /// ~1.1 GB, so an unranked scan of it would otherwise run for minutes.
    /// </para>
    ///
    /// <para>
    /// Zero or negative disables ESI resolution entirely — the budget is exhausted before the
    /// first candidate file is ever opened, so every lookup reports the device as not found —
    /// while <see cref="Directory"/> still looks fully configured. <see cref="EsiCatalog"/> logs
    /// one Warning at startup when this silently-inert combination is configured.
    /// </para>
    /// </summary>
    public int LookupBudgetMs { get; set; } = 5000;
}
