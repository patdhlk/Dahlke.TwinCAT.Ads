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
///   <item><description>Structs, function blocks and unions —
///     <c>Dictionary&lt;string, object?&gt;</c> keyed by each sub-symbol's
///     <see cref="IInstance.InstanceName"/>.</description></item>
///   <item><description>Arrays — <c>object?[]</c> with each element decoded recursively, in
///     element order whatever the PLC array's lower bound.</description></item>
///   <item><description>Enums — the numeric backing value.</description></item>
///   <item><description><see langword="null"/> — <see langword="null"/>.</description></item>
/// </list>
/// <para>
/// <b>Categories this decoder does NOT transform</b> pass through in Beckhoff's own shape:
/// <see cref="DataTypeCategory.Alias"/>, <see cref="DataTypeCategory.Program"/>,
/// <see cref="DataTypeCategory.Pointer"/> and <see cref="DataTypeCategory.Reference"/>. For an
/// alias to a primitive that is correct; for the rest it is a known degradation, documented on
/// <c>AdsConnection.IsContainer</c> along with why routing them here was not attempted.
/// </para>
/// <para>
/// <b>The value first, the wire only for what it cannot supply.</b> A container read through its
/// symbol's value accessor — or decoded from a notification payload — arrives as Beckhoff's
/// <c>DynamicValue</c>: the container's bytes together with the symbol's type information. Its
/// members (<see cref="IStructValue.TryGetMemberValue"/>) and elements are decoded from those
/// bytes, so the whole tree is built from the ONE read that fetched the container. PLC struct
/// packing, string encoding and enum backing types are still handled by the TwinCAT symbol layer,
/// not reimplemented here. A sub-symbol is read on its own only when the value in hand cannot
/// supply it: a member the value does not carry, or an array value whose elements do not line up
/// with the array's element sub-symbols (a raw byte buffer). An earlier version read EVERY member
/// and element that way and never consulted the value — one ADS round-trip per member, which for a
/// status struct of a hundred members took seconds per read and let a notification subscription
/// fall arbitrarily far behind.
/// </para>
/// <para>
/// <b>Async and cancellable, all the way down.</b> Every member / element read that does happen
/// goes through <see cref="IValueSymbol.ReadValueAsync(System.Threading.CancellationToken)"/>,
/// threading the SAME <see cref="System.Threading.CancellationToken"/> passed to the top-level
/// <see cref="DecodeAsync"/> call down through every recursive call, so a caller's timeout budget
/// bounds every read, not just the first. <c>ReadValue()</c> is not called anywhere in this type.
/// </para>
/// </remarks>
internal static class PlcValueDecoder
{
    /// <summary>
    /// Decodes <paramref name="value"/> using <paramref name="symbol"/>'s metadata. Members and
    /// elements come from <paramref name="value"/> itself wherever it carries them; any it does not
    /// are read from their own sub-symbols via <paramref name="ct"/>-bound async reads.
    /// </summary>
    public static async Task<object?> DecodeAsync(object? value, ISymbol symbol, CancellationToken ct)
    {
        if (value is null)
            return null;

        if (HasMembers(symbol))
            return await DecodeStructAsync(value as IStructValue, symbol, ct).ConfigureAwait(false);

        if (symbol.Category is DataTypeCategory.Array && TryGetElements(value, out var elements))
            return await DecodeArrayAsync(elements, symbol, ct).ConfigureAwait(false);

        // Primitives, strings, enums, opaque containers and everything else: pass through.
        return value;
    }

    /// <summary>
    /// Decodes <paramref name="value"/> WITHOUT performing any ADS I/O, for callers that cannot
    /// await — notably the synchronous ADS notification handler in <c>AdsConnection</c>. Returns
    /// <see langword="true"/> and the decoded tree when <paramref name="value"/> itself supplies
    /// everything <see cref="DecodeAsync"/> would produce, and <see langword="false"/> (with
    /// <paramref name="decoded"/> set to <see langword="null"/>) when some member or element would
    /// have to be read from its sub-symbol — in which case the caller must move the decode
    /// somewhere it can await.
    /// </summary>
    /// <remarks>
    /// This mirrors <see cref="DecodeAsync"/> branch for branch and differs only in refusing where
    /// <see cref="DecodeAsync"/> would read, so a value decoded here is identical to one decoded
    /// there. A notification payload decodes to a <c>DynamicValue</c> that carries every member,
    /// so a struct or array subscription is served inline, on the notification thread, with no
    /// round-trip.
    /// </remarks>
    public static bool TryDecodeWithoutReads(object? value, ISymbol symbol, out object? decoded)
    {
        decoded = null;

        // A null value decodes to null on every path — DecodeAsync's own first check.
        if (value is null)
            return true;

        if (HasMembers(symbol))
        {
            if (value is not IStructValue members)
                return false;

            var dict = new Dictionary<string, object?>(symbol.SubSymbols.Count);
            foreach (var sub in symbol.SubSymbols)
            {
                if (sub is not IValueSymbol)
                    continue;
                if (!members.TryGetMemberValue(sub.InstanceName, out var member)
                    || !TryDecodeWithoutReads(member, sub, out var decodedMember))
                {
                    return false;
                }
                dict[sub.InstanceName] = decodedMember;
            }
            decoded = dict;
            return true;
        }

        if (symbol.Category is DataTypeCategory.Array && TryGetElements(value, out var elements))
        {
            var subSymbols = symbol.SubSymbols;
            if (subSymbols.Count == 0)
            {
                decoded = elements.ToArray();
                return true;
            }
            if (subSymbols.Count != elements.Count)
                return false;   // DecodeAsync reads every element from its sub-symbol here

            var result = new object?[elements.Count];
            var index = 0;
            foreach (var sub in subSymbols)
            {
                if (sub is IValueSymbol)
                {
                    if (!TryDecodeWithoutReads(elements[index], sub, out result[index]))
                        return false;
                }
                else
                {
                    result[index] = elements[index];
                }
                index++;
            }
            decoded = result;
            return true;
        }

        // Primitives, strings, enums and opaque containers are returned unchanged by DecodeAsync.
        decoded = value;
        return true;
    }

    /// <summary>
    /// True when <paramref name="symbol"/> is a struct, function block or union that exposes at
    /// least one sub-symbol, i.e. one <see cref="DecodeAsync"/> rebuilds as a dictionary rather than
    /// passing through. An opaque one — no sub-symbols — passes its raw value through unchanged.
    /// </summary>
    /// <remarks>
    /// <b>Unions decode like structs.</b> A union's members are ordinary readable sub-symbols that
    /// merely overlap in storage, and Beckhoff's value factory wraps a union in the same
    /// <c>DynamicValue</c> it wraps a struct in — so passing one through would put a TwinCAT type
    /// on a surface documented as neutral. Decoding each member independently is well defined; the
    /// members simply reinterpret the same bytes, which is what a union means.
    /// </remarks>
    public static bool HasMembers(ISymbol symbol) =>
        symbol.Category is DataTypeCategory.Struct or DataTypeCategory.FunctionBlock or DataTypeCategory.Union
        && symbol.SubSymbols.Count > 0;

    /// <summary>
    /// True when decoding <paramref name="symbol"/> wants the value from its symbol's own value
    /// accessor — a <c>DynamicValue</c> it can walk — rather than the raw bytes a plain client
    /// read returns: structs, function blocks and unions with sub-symbols, and arrays.
    /// </summary>
    /// <remarks>
    /// <c>AdsClient.ReadValueAsync(ISymbol)</c> returns a container as a raw <c>byte[]</c>, which
    /// carries no members and whose "elements" are bytes; the symbol's
    /// <see cref="IValueSymbol.ReadValueAsync(CancellationToken)"/> returns the same single
    /// round-trip decoded into a <c>DynamicValue</c> (struct) or an array of element values. Opaque
    /// structs keep the raw read, since they pass their raw value through unchanged.
    /// </remarks>
    public static bool WantsSymbolicRead(ISymbol symbol) =>
        HasMembers(symbol) || symbol.Category is DataTypeCategory.Array;

    private static async Task<Dictionary<string, object?>> DecodeStructAsync(
        IStructValue? members, ISymbol symbol, CancellationToken ct)
    {
        var dict = new Dictionary<string, object?>(symbol.SubSymbols.Count);

        foreach (var sub in symbol.SubSymbols)
        {
            if (sub is IValueSymbol valueSub)
            {
                var subValue = members is not null && members.TryGetMemberValue(sub.InstanceName, out var local)
                    ? local
                    : await ReadMemberAsync(valueSub, sub, ct).ConfigureAwait(false);
                dict[sub.InstanceName] = await DecodeAsync(subValue, sub, ct).ConfigureAwait(false);
            }
        }

        return dict;
    }

    private static async Task<object?[]> DecodeArrayAsync(IReadOnlyList<object?> elements, ISymbol symbol, CancellationToken ct)
    {
        var subSymbols = symbol.SubSymbols;

        if (subSymbols.Count == 0)
            return elements.ToArray();   // no element metadata: the values are all there is

        var result = new object?[subSymbols.Count];
        var lineUp = subSymbols.Count == elements.Count;
        var index = 0;
        foreach (var sub in subSymbols)
        {
            if (sub is IValueSymbol valueSub)
            {
                // The elements line up with the element sub-symbols: decode each from the value.
                // They do not (a raw byte buffer, one "element" per byte): read each element
                // itself instead of returning bytes that look like data.
                var subValue = lineUp ? elements[index] : await ReadMemberAsync(valueSub, sub, ct).ConfigureAwait(false);
                result[index] = await DecodeAsync(subValue, sub, ct).ConfigureAwait(false);
            }
            else
            {
                result[index] = lineUp ? elements[index] : null;
            }
            index++;
        }

        return result;
    }

    /// <summary>
    /// The elements of an array value in storage order: a CLR <see cref="Array"/> of any rank and
    /// lower bound (enumerated, never indexed from 0 — a PLC <c>ARRAY[1..4]</c> arrives as a
    /// 1-based array), or a <c>DynamicValue</c> standing for an array.
    /// </summary>
    private static bool TryGetElements(object value, out IReadOnlyList<object?> elements)
    {
        switch (value)
        {
            case Array array:
                var list = new List<object?>(array.Length);
                foreach (var element in array)
                    list.Add(element);
                elements = list;
                return true;
            case DynamicValue dynamicValue when dynamicValue.TryGetArrayElementValues(out var values):
                elements = values.ToList();
                return true;
            default:
                elements = [];
                return false;
        }
    }

    /// <summary>
    /// Reads one struct member or array element via the cancellable, async
    /// <see cref="IValueSymbol.ReadValueAsync(CancellationToken)"/> and throws
    /// <see cref="AdsErrorException"/> if the read failed — <c>ReadValueAsync</c> uses the
    /// non-throwing Result pattern, so the failure is made explicit here.
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
