namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Pure, side-effect-free helper for deciding whether a symbol should be
/// included in a diagnostic dump. Extracted so it can be unit-tested
/// without a live PLC connection.
/// </summary>
internal static class SymbolDumpFilter
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="instancePath"/> should
    /// be included in a symbol dump governed by <paramref name="options"/>.
    /// </summary>
    /// <remarks>
    /// Inclusion criteria (both must hold):
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       The depth of <paramref name="instancePath"/> — measured as the
    ///       number of <c>'.'</c> characters — is at most
    ///       <see cref="SymbolDumpOptions.MaxDepth"/>.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <see cref="SymbolDumpOptions.Prefixes"/> is empty (all top-level
    ///       namespaces are allowed), OR at least one prefix matches the start
    ///       of <paramref name="instancePath"/> (case-insensitive,
    ///       <see cref="StringComparison.OrdinalIgnoreCase"/>).
    ///     </description>
    ///   </item>
    /// </list>
    /// </remarks>
    internal static bool ShouldInclude(string instancePath, SymbolDumpOptions options)
    {
        var depth = instancePath.AsSpan().Count('.');
        if (depth > options.MaxDepth)
            return false;

        if (options.Prefixes.Count == 0)
            return true;

        foreach (var prefix in options.Prefixes)
        {
            // Configuration binding can insert null entries from a malformed array.
            if (!string.IsNullOrEmpty(prefix) &&
                instancePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
