namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Neutral metadata for one PLC symbol, free of any TwinCAT type-system dependency so that
/// consumers can project it into their own models without referencing Beckhoff types.
/// </summary>
/// <param name="InstancePath">Fully-qualified symbol path, for example <c>MAIN.Motor.Speed</c>.</param>
/// <param name="TypeName">The symbol's PLC type name, for example <c>INT</c> or <c>ST_Motor</c>.</param>
/// <param name="Category">The symbol's type category, for example <c>Primitive</c>, <c>Struct</c>, <c>Array</c>, <c>Enum</c>.</param>
/// <param name="ByteSize">The symbol's size in bytes.</param>
/// <param name="Comment">The declaration comment from the PLC project, or <see langword="null"/> when absent.</param>
/// <param name="Children">
/// Nested sub-symbols for containers, or <see langword="null"/> when children were not
/// requested or the symbol is a leaf. An empty list is never returned — a childless symbol
/// yields <see langword="null"/>.
/// </param>
public sealed record AdsSymbolInfo(
    string InstancePath,
    string TypeName,
    string Category,
    int ByteSize,
    string? Comment,
    IReadOnlyList<AdsSymbolInfo>? Children)
{
    /// <summary>
    /// Declaration attributes (PLC pragmas) for this symbol, keyed by attribute name with the
    /// attribute's value as the entry. Keys and values are plain strings, so the record keeps its
    /// freedom from any TwinCAT type-system dependency.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> means attributes were not collected for this symbol — an empty
    /// dictionary means they were collected and the symbol carries none. Consumers that treat the
    /// absence of an attribute as a decision (for example OPC UA symbol release) must distinguish
    /// the two, because "not collected" is not "not present".
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Attributes { get; init; }
}
