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
                .OrderByDescending(f => CommonPrefixLength(model, f.name))
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

    private static int CommonPrefixLength(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length), i = 0;
        while (i < n && char.ToUpperInvariant(a[i]) == char.ToUpperInvariant(b[i]))
        {
            i++;
        }

        return i;
    }
}
