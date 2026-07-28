using TwinCAT.Ads;
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
/// <para>
/// <b>Async and cancellable, all the way down.</b> Every struct member / array element read
/// goes through <see cref="IValueSymbol.ReadValueAsync(System.Threading.CancellationToken)"/>,
/// threading the SAME <see cref="System.Threading.CancellationToken"/> passed to the top-level
/// <see cref="DecodeAsync"/> call down through every recursive call. A caller's timeout budget
/// (for example <c>AdsConnection.ReadValuesAsync</c>'s whole-batch
/// <see cref="System.Threading.CancellationTokenSource"/>) therefore bounds every member/element
/// read, not just the first one — an earlier version of this decoder used the synchronous,
/// non-cancellable <c>ISymbol.ReadValue()</c> internally, which let a struct with many members
/// block past the configured timeout with no way to cancel. <c>ReadValue()</c> is no longer
/// called anywhere in this type.
/// </para>
/// </remarks>
internal static class PlcValueDecoder
{
    /// <summary>
    /// Decodes <paramref name="value"/> using <paramref name="symbol"/>'s metadata, reading any
    /// struct/function-block members or array elements via <paramref name="ct"/>-bound async
    /// reads on their own sub-symbols.
    /// </summary>
    public static async Task<object?> DecodeAsync(object? value, ISymbol symbol, CancellationToken ct)
    {
        if (value is null)
            return null;

        var category = symbol.Category;

        // Structs / function blocks: iterate sub-symbols and recurse.
        if (category is DataTypeCategory.Struct or DataTypeCategory.FunctionBlock
            && symbol.SubSymbols.Count > 0)
        {
            return await DecodeStructAsync(symbol, ct).ConfigureAwait(false);
        }

        // Arrays: map each element, using sub-symbols when available.
        if (category is DataTypeCategory.Array && value is Array array)
        {
            return await DecodeArrayAsync(array, symbol, ct).ConfigureAwait(false);
        }

        // Primitives, strings, enums, and everything else: pass through.
        return value;
    }

    /// <summary>
    /// Decodes <paramref name="value"/> WITHOUT performing any ADS I/O, for callers that cannot
    /// await — notably the synchronous ADS notification handler in <c>AdsConnection</c>.
    /// Returns <see langword="true"/> and the decoded value when <see cref="DecodeAsync"/> would
    /// have passed <paramref name="value"/> straight through, and <see langword="false"/> (with
    /// <paramref name="decoded"/> set to <see langword="null"/>) when decoding genuinely needs
    /// <see cref="DecodeAsync"/> — in which case the caller must move the decode somewhere it can
    /// await.
    /// </summary>
    /// <remarks>
    /// This is deliberately NOT a second decoder: the pass-through branch below is the same
    /// branch <see cref="DecodeAsync"/> ends on, so a value decoded here is identical to one
    /// decoded there. Only the container shapes — which <see cref="DecodeAsync"/> genuinely
    /// transforms, reading one member/element at a time — are refused.
    /// </remarks>
    public static bool TryDecodeWithoutReads(object? value, ISymbol symbol, out object? decoded)
    {
        // A null value decodes to null on every path — DecodeAsync's own first check.
        if (value is null)
        {
            decoded = null;
            return true;
        }

        // Containers are the shapes DecodeAsync actually transforms: a struct/function block with
        // sub-symbols is rebuilt as a dictionary by reading each member (one ADS read per member),
        // and an array is rebuilt as an object?[] element by element (reading per-element
        // sub-symbols when they line up). Neither is a pass-through, so neither can be served
        // without I/O — even the array whose sub-symbols do not line up still needs the rebuild.
        if (symbol.Category is DataTypeCategory.Array || DecodesFromSubSymbolsOnly(symbol))
        {
            decoded = null;
            return false;
        }

        // Everything else — primitives, strings, enums, and opaque structs/function blocks with no
        // sub-symbols — is returned unchanged by DecodeAsync, so decoding needs no I/O at all.
        decoded = value;
        return true;
    }

    /// <summary>
    /// True when decoding <paramref name="symbol"/> reads entirely from its own sub-symbols and
    /// never consumes an externally supplied <c>value</c> — this holds for structs and function
    /// blocks that expose at least one sub-symbol. A caller may use this to skip fetching its own
    /// top-level raw value for <paramref name="symbol"/> before calling <see cref="DecodeAsync"/>:
    /// <see cref="DecodeAsync"/>'s <c>value</c> parameter is only ever consulted for a null check
    /// in that case, never returned or inspected further, so any non-null placeholder satisfies
    /// it. Arrays (which need the raw value for <c>Array.Length</c> and element access) and
    /// opaque structs/function blocks with no sub-symbols (which pass the raw value through
    /// unchanged) both still need a genuine externally supplied value, so this returns
    /// <see langword="false"/> for them.
    /// </summary>
    public static bool DecodesFromSubSymbolsOnly(ISymbol symbol) =>
        symbol.Category is DataTypeCategory.Struct or DataTypeCategory.FunctionBlock
        && symbol.SubSymbols.Count > 0;

    private static async Task<Dictionary<string, object?>> DecodeStructAsync(ISymbol symbol, CancellationToken ct)
    {
        var dict = new Dictionary<string, object?>(symbol.SubSymbols.Count);

        foreach (var sub in symbol.SubSymbols)
        {
            if (sub is IValueSymbol valueSub)
            {
                var subValue = await ReadMemberAsync(valueSub, sub, ct).ConfigureAwait(false);
                dict[sub.InstanceName] = await DecodeAsync(subValue, sub, ct).ConfigureAwait(false);
            }
        }

        return dict;
    }

    private static async Task<object?[]> DecodeArrayAsync(Array array, ISymbol symbol, CancellationToken ct)
    {
        var result = new object?[array.Length];
        var subSymbols = symbol.SubSymbols;

        if (subSymbols.Count == array.Length)
        {
            // Sub-symbols available per element — use them for full type info.
            var index = 0;
            foreach (var sub in subSymbols)
            {
                if (sub is IValueSymbol valueSub)
                {
                    var subValue = await ReadMemberAsync(valueSub, sub, ct).ConfigureAwait(false);
                    result[index] = await DecodeAsync(subValue, sub, ct).ConfigureAwait(false);
                }
                else
                {
                    result[index] = array.GetValue(index);
                }
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

    /// <summary>
    /// Reads one struct member or array element via the cancellable, async
    /// <see cref="IValueSymbol.ReadValueAsync(CancellationToken)"/> and throws
    /// <see cref="AdsErrorException"/> if the read failed — matching the throwing convention the
    /// old synchronous <c>ReadValue()</c> provided implicitly (Beckhoff's simple/throwing
    /// overload), now made explicit because <c>ReadValueAsync</c> uses the non-throwing
    /// Result pattern instead.
    /// </summary>
    private static async Task<object?> ReadMemberAsync(IValueSymbol valueSub, ISymbol sub, CancellationToken ct)
    {
        var result = await valueSub.ReadValueAsync(ct).ConfigureAwait(false);
        if (result.Failed)
            throw new AdsErrorException(
                $"Read of PLC member '{sub.InstanceName}' failed: {(AdsErrorCode)result.ErrorCode}",
                (AdsErrorCode)result.ErrorCode);

        return result.Value;
    }
}
