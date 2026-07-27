using TwinCAT.TypeSystem;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Decodes a value read from the PLC, together with its <see cref="ISymbol"/> metadata,
/// into a neutral tree of plain .NET types.
/// </summary>
/// <remarks>
/// <para>
/// The result contains only types a serializer can handle without knowing anything about
/// TwinCAT:
/// </para>
/// <list type="bullet">
///   <item><description>Primitives and strings — returned as-is.</description></item>
///   <item><description>Structs and function blocks — <c>Dictionary&lt;string, object?&gt;</c>
///     keyed by each sub-symbol's <see cref="IInstance.InstanceName"/>.</description></item>
///   <item><description>Arrays — <c>object?[]</c> with each element decoded recursively.</description></item>
///   <item><description>Enums — the numeric backing value.</description></item>
///   <item><description><see langword="null"/> — <see langword="null"/>.</description></item>
/// </list>
/// <para>
/// Struct and array members are read through their sub-symbols rather than unpacked from
/// raw bytes, so PLC struct packing, string encoding and enum backing types are handled by
/// the TwinCAT symbol layer instead of being reimplemented here.
/// </para>
/// </remarks>
internal static class PlcValueDecoder
{
    /// <summary>Decodes <paramref name="value"/> using <paramref name="symbol"/>'s metadata.</summary>
    public static object? Decode(object? value, ISymbol symbol)
    {
        if (value is null)
            return null;

        var category = symbol.Category;

        // Structs / function blocks: iterate sub-symbols and recurse.
        if (category is DataTypeCategory.Struct or DataTypeCategory.FunctionBlock
            && symbol.SubSymbols.Count > 0)
        {
            return DecodeStruct(symbol);
        }

        // Arrays: map each element, using sub-symbols when available.
        if (category is DataTypeCategory.Array && value is Array array)
        {
            return DecodeArray(array, symbol);
        }

        // Primitives, strings, enums, and everything else: pass through.
        return value;
    }

    private static Dictionary<string, object?> DecodeStruct(ISymbol symbol)
    {
        var dict = new Dictionary<string, object?>(symbol.SubSymbols.Count);

        foreach (var sub in symbol.SubSymbols)
        {
            if (sub is IValueSymbol valueSub)
            {
                var subValue = valueSub.ReadValue();
                dict[sub.InstanceName] = Decode(subValue, sub);
            }
        }

        return dict;
    }

    private static object?[] DecodeArray(Array array, ISymbol symbol)
    {
        var result = new object?[array.Length];
        var subSymbols = symbol.SubSymbols;

        if (subSymbols.Count == array.Length)
        {
            // Sub-symbols available per element — use them for full type info.
            var index = 0;
            foreach (var sub in subSymbols)
            {
                result[index] = sub is IValueSymbol valueSub
                    ? Decode(valueSub.ReadValue(), sub)
                    : array.GetValue(index);
                index++;
            }
        }
        else
        {
            // Fallback: no matching sub-symbols, copy raw values.
            for (var i = 0; i < array.Length; i++)
                result[i] = array.GetValue(i);
        }

        return result;
    }
}
