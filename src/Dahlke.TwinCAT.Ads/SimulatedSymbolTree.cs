using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Derives a browsable symbol tree from a flat, dotted-path
/// <see cref="InMemoryPlcStore{TKey, TValue}"/>: <c>MAIN.Motor.Speed</c> yields a
/// <c>MAIN</c> container holding a <c>MAIN.Motor</c> container holding the
/// <c>MAIN.Motor.Speed</c> leaf. Shared by every simulated/in-memory symbol data
/// plane so the derivation exists once.
/// </summary>
/// <remarks>
/// Container nodes are synthetic — they have no stored value — and report type
/// <c>STRUCT</c>; leaves map through <see cref="SimulatedAdsConnection.InferPlcType"/>,
/// the same inference a simulated metadata read uses. Paths compare
/// case-insensitively (PLC symbol paths are case-insensitive), and a mis-cased
/// lookup reports <see cref="AdsSymbolInfo.InstancePath"/> in the casing the
/// symbol was actually seeded with, never echoing the caller's spelling.
/// Every built <see cref="AdsSymbolInfo.Attributes"/> comes from the caller-supplied
/// attribute map — this module reads it but never stores it, so it stays a pure
/// function of its inputs. A path absent from that map reports an empty attribute
/// set, never <see langword="null"/>, matching the "simulation always collects"
/// contract documented on <see cref="PlcTargetOptions.SymbolAttributes"/>.
/// </remarks>
internal static class SimulatedSymbolTree
{
    /// <summary>
    /// Lists the child symbols under <paramref name="parentPath"/> (or the roots
    /// when null/empty), ordered case-insensitively.
    /// </summary>
    /// <exception cref="AdsErrorException">
    /// With <see cref="AdsErrorCode.DeviceSymbolNotFound"/> when nothing is
    /// stored at or beneath <paramref name="parentPath"/>.
    /// </exception>
    public static IReadOnlyList<AdsSymbolInfo> GetSymbols(
        InMemoryPlcStore<string, object?> store,
        IReadOnlyDictionary<string, Dictionary<string, string>> attributes,
        string? parentPath, bool includeChildren)
    {
        var prefix = string.Empty;
        if (!string.IsNullOrEmpty(parentPath))
        {
            var canonicalParent = ResolveStoredCasing(store, parentPath)
                ?? throw new AdsErrorException(
                    $"Symbol '{parentPath}' not found.", AdsErrorCode.DeviceSymbolNotFound);
            prefix = canonicalParent + ".";
        }

        return ChildNames(store, prefix)
            .Select(name => BuildSymbolInfo(store, attributes, prefix + name, includeChildren))
            .ToList();
    }

    /// <summary>
    /// Case-insensitive substring match over every stored leaf path plus every
    /// synthetic container path above it; cost is proportional to the number of
    /// stored symbols.
    /// </summary>
    public static IReadOnlyList<AdsSymbolInfo> Search(
        InMemoryPlcStore<string, object?> store,
        IReadOnlyDictionary<string, Dictionary<string, string>> attributes,
        string pattern, bool includeChildren)
        => AllPaths(store)
            .Where(p => p.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => BuildSymbolInfo(store, attributes, p, includeChildren))
            .ToList();

    /// <summary>
    /// Resolves <paramref name="path"/> to its as-stored casing by locating a
    /// stored key at or beneath it, or <see langword="null"/> when nothing is
    /// stored there.
    /// </summary>
    private static string? ResolveStoredCasing(InMemoryPlcStore<string, object?> store, string path)
    {
        foreach (var key in store.Keys)
        {
            if (key.Equals(path, StringComparison.OrdinalIgnoreCase))
                return key;
            if (key.Length > path.Length && key[path.Length] == '.' &&
                key.AsSpan(0, path.Length).Equals(path, StringComparison.OrdinalIgnoreCase))
                return key[..path.Length];
        }
        return null;
    }

    /// <summary>Every stored leaf path plus every synthetic container path above it.</summary>
    private static IEnumerable<string> AllPaths(InMemoryPlcStore<string, object?> store)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in store.Keys)
        {
            var segments = key.Split('.');
            for (var i = 1; i <= segments.Length; i++)
                paths.Add(string.Join('.', segments.Take(i)));
        }
        return paths;
    }

    private static List<string> ChildNames(InMemoryPlcStore<string, object?> store, string prefix)
        => store.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.Length > prefix.Length)
            .Select(k => k.Substring(prefix.Length).Split('.')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static AdsSymbolInfo BuildSymbolInfo(
        InMemoryPlcStore<string, object?> store,
        IReadOnlyDictionary<string, Dictionary<string, string>> attributes,
        string path, bool includeChildren)
    {
        var isLeaf = store.TryRead(path, out var value);
        var (typeName, category) = isLeaf
            ? SimulatedAdsConnection.InferPlcType(value)
            : ("STRUCT", "Struct");

        List<AdsSymbolInfo>? children = null;
        if (includeChildren)
        {
            var childNames = ChildNames(store, path + ".");
            if (childNames.Count > 0)
                children = childNames
                    .Select(n => BuildSymbolInfo(store, attributes, path + "." + n, includeChildren: true))
                    .ToList();
        }

        return new AdsSymbolInfo(path, typeName, category, ByteSize: 0, Comment: null, children)
        {
            Attributes = attributes.TryGetValue(path, out var forSymbol)
                ? new Dictionary<string, string>(forSymbol, StringComparer.OrdinalIgnoreCase)
                : EmptyAttributes,
        };
    }

    /// <summary>Shared empty result for a symbol with no seeded attributes — simulation always
    /// "collects" attributes, so this is reported instead of <see langword="null"/>.</summary>
    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
