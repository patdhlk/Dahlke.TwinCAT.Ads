namespace Dahlke.EtherCAT.Esi;

/// <summary>
/// Orders ESI file names by how likely each is to contain a given device, using the device's
/// inferred type string as a hint. PURE — performs no I/O, so the heuristic is exhaustively
/// testable without a directory tree.
/// </summary>
/// <remarks>
/// <para>
/// Ranking decides only the ORDER files are searched; <see cref="EsiDeviceReader"/>'s identity
/// match alone decides the answer. A useless hint therefore costs time, never correctness — which
/// is what makes it safe to rank on a type string adsify INFERRED (via
/// <c>BeckhoffDeviceDecoder.DecodeDeviceType</c>) rather than read off the device.
/// </para>
/// <para>
/// <see cref="Rank"/> returns EVERY input path, not just the prefix matches. The bounded fallback
/// in <see cref="EsiCatalog"/> depends on that: a device whose filename this heuristic ranks last
/// is still present in the set and must still be findable. Filtering here would turn a ranking
/// miss into a wrong answer indistinguishable from genuine absence.
/// </para>
/// </remarks>
internal static class EsiCandidateRanker
{
    private const string VendorPrefix = "Beckhoff ";
    private static readonly char[] TypeSeparators = [' ', '|', '\t'];

    public static IReadOnlyList<string> Rank(IEnumerable<string> filePaths, string typeHint)
    {
        List<string> all = [.. filePaths];
        string model = Model(typeHint);
        string prefix = new(model.TakeWhile(char.IsLetter).ToArray());

        IEnumerable<string> byFamily = prefix.Length == 0
            ? []
            : all.Select(path => (path, name: Strip(Path.GetFileNameWithoutExtension(path))))
                .Where(f => f.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => MatchLength(model, f.name))
                // Reading x as a digit makes whole groups of names match to the same length, so
                // this is now the key that separates them — and it separates them the right way
                // round on its own: 'X' sorts after every digit, so the file that spells the digit
                // out beats the file that wildcards it. Same ordinal fact that used to bury
                // ELx9xx, once the score above stops burying it first.
                .ThenBy(f => f.name, StringComparer.OrdinalIgnoreCase)
                .Select(f => f.path);

        // Combined catalogs ("… EtherCAT Terminals.xml") carry many families, so they are a better
        // guess than an unrelated vendor file once the family files are exhausted.
        IEnumerable<string> combined = all
            .Where(path => Path.GetFileNameWithoutExtension(path)
                .Contains("EtherCAT", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> rest = all.OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        return [.. byFamily.Concat(combined).Concat(rest).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The model token of a type string. <c>DecodeDeviceType</c> yields a bare model ("EL3204"),
    /// but "Unknown" / "Vendor(0x1234)" for non-Beckhoff vendors, and callers may pass a decorated
    /// string ("EL3204 | 4Ch. Ana. Input") — so only the leading token counts.
    /// </summary>
    private static string Model(string typeHint)
    {
        if (string.IsNullOrWhiteSpace(typeHint))
        {
            return string.Empty;
        }

        string[] tokens = typeHint.Split(TypeSeparators, StringSplitOptions.RemoveEmptyEntries);

        return tokens.Length == 0 ? string.Empty : tokens[0];
    }

    private static string Strip(string fileName) =>
        fileName.StartsWith(VendorPrefix, StringComparison.OrdinalIgnoreCase)
            ? fileName[VendorPrefix.Length..]
            : fileName;

    /// <summary>
    /// How far a file name tracks the model, reading the <c>x</c> Beckhoff writes where a family
    /// digit would go as a stand-in for that digit: <c>ELx9xx</c> tracks <c>EL1904</c> all six
    /// characters, not the two a literal comparison sees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That <c>x</c> is not noise in the vendor set — it marks a file that deliberately spans
    /// families, and Beckhoff files EVERY TwinSAFE I/O terminal (EK1914, EL1904, EL2904, EP1908)
    /// into one such file rather than into each family's own. Scored literally, those four share
    /// only the leading letters with their own model, so the single file that holds them ranked
    /// LAST within its family group instead of near the front: candidate 39 of 142 on the
    /// reference set, 605 MB streamed and 3.3 s spent before the device was found. It made safety
    /// terminals the devices least likely to resolve inside the lookup budget, which is the wrong
    /// way round.
    /// </para>
    /// <para>
    /// The wildcard stands for a digit and ONLY a digit. That limit is what keeps a longer, vaguer
    /// name from winning on length where the model runs past the end of a shorter one: given
    /// "EL1904-0000", an unrestricted <c>x</c> would let <c>ELXxxxx</c> match the <c>-</c> as well
    /// and so outscore <c>EL19xx</c>.
    /// </para>
    /// </remarks>
    private static int MatchLength(string model, string name)
    {
        int n = Math.Min(model.Length, name.Length), i = 0;

        while (i < n
            && (char.ToUpperInvariant(model[i]) == char.ToUpperInvariant(name[i])
                || (name[i] is 'x' or 'X' && char.IsAsciiDigit(model[i]))))
        {
            i++;
        }

        return i;
    }
}
