using TwinCAT.Ads;
using TwinCAT.TypeSystem;

namespace Dahlke.TwinCAT.Ads.Tests;

public class PlcValueDecoderTests
{
    [Fact]
    public async Task Decode_returns_null_for_null_value()
    {
        var symbol = new StubSymbol(DataTypeCategory.Primitive, "INT");

        Assert.Null(await PlcValueDecoder.DecodeAsync(null, symbol, CancellationToken.None));
    }

    [Fact]
    public async Task Decode_passes_primitives_through_unchanged()
    {
        var symbol = new StubSymbol(DataTypeCategory.Primitive, "INT");

        Assert.Equal(42, await PlcValueDecoder.DecodeAsync(42, symbol, CancellationToken.None));
    }

    [Fact]
    public async Task Decode_passes_strings_through_unchanged()
    {
        var symbol = new StubSymbol(DataTypeCategory.String, "STRING(80)");

        Assert.Equal("hello", await PlcValueDecoder.DecodeAsync("hello", symbol, CancellationToken.None));
    }

    [Fact]
    public async Task Decode_passes_enums_through_as_backing_value()
    {
        var symbol = new StubSymbol(DataTypeCategory.Enum, "E_Mode");

        Assert.Equal(2, await PlcValueDecoder.DecodeAsync(2, symbol, CancellationToken.None));
    }

    [Fact]
    public async Task Decode_converts_struct_to_dictionary_keyed_by_instance_name()
    {
        var symbol = new StubSymbol(DataTypeCategory.Struct, "ST_Motor",
            new StubValueSymbol("Speed", DataTypeCategory.Primitive, "INT", 1500),
            new StubValueSymbol("Running", DataTypeCategory.Primitive, "BOOL", true));

        var decoded = Assert.IsType<Dictionary<string, object?>>(
            await PlcValueDecoder.DecodeAsync(new object(), symbol, CancellationToken.None));

        Assert.Equal(2, decoded.Count);
        Assert.Equal(1500, decoded["Speed"]);
        Assert.Equal(true, decoded["Running"]);
    }

    /// <summary>
    /// A UNION's members are ordinary readable sub-symbols that happen to overlap in storage, and
    /// Beckhoff's value factory wraps a union in the same <c>DynamicValue</c> it wraps a struct in.
    /// Passing that through untouched would put a TwinCAT type on a surface documented as neutral —
    /// the one thing this decoder exists to prevent — so a union decodes by the same sub-symbol
    /// walk a struct does.
    /// </summary>
    [Fact]
    public async Task Decode_converts_union_to_dictionary_keyed_by_instance_name()
    {
        var symbol = new StubSymbol(DataTypeCategory.Union, "U_Payload",
            new StubValueSymbol("AsInt", DataTypeCategory.Primitive, "DINT", 1145258561),
            new StubValueSymbol("AsReal", DataTypeCategory.Primitive, "REAL", 1000.5f));

        var decoded = Assert.IsType<Dictionary<string, object?>>(
            await PlcValueDecoder.DecodeAsync(new object(), symbol, CancellationToken.None));

        Assert.Equal(2, decoded.Count);
        Assert.Equal(1145258561, decoded["AsInt"]);
        Assert.Equal(1000.5f, decoded["AsReal"]);
    }

    /// <summary>
    /// An opaque union — no sub-symbols to walk — passes through, exactly as an opaque struct does.
    /// </summary>
    [Fact]
    public async Task Decode_passes_opaque_union_through_unchanged()
    {
        var symbol = new StubSymbol(DataTypeCategory.Union, "U_Opaque");
        var raw = new object();

        Assert.Same(raw, await PlcValueDecoder.DecodeAsync(raw, symbol, CancellationToken.None));
    }

    [Fact]
    public async Task TryDecodeWithoutReads_refuses_a_union_with_sub_symbols()
    {
        var symbol = new StubSymbol(DataTypeCategory.Union, "U_Payload",
            new StubValueSymbol("AsInt", DataTypeCategory.Primitive, "DINT", 1));

        Assert.False(PlcValueDecoder.TryDecodeWithoutReads(new object(), symbol, out var decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public async Task Decode_converts_nested_struct_recursively()
    {
        var inner = new StubValueSymbol("Inner", DataTypeCategory.Struct, "ST_Inner", new object(),
            new StubValueSymbol("Depth", DataTypeCategory.Primitive, "INT", 7));
        var outer = new StubSymbol(DataTypeCategory.Struct, "ST_Outer", inner);

        var decoded = Assert.IsType<Dictionary<string, object?>>(
            await PlcValueDecoder.DecodeAsync(new object(), outer, CancellationToken.None));
        var nested = Assert.IsType<Dictionary<string, object?>>(decoded["Inner"]);

        Assert.Equal(7, nested["Depth"]);
    }

    [Fact]
    public async Task Decode_converts_array_using_sub_symbols_when_counts_match()
    {
        var symbol = new StubSymbol(DataTypeCategory.Array, "ARRAY [0..1] OF INT",
            new StubValueSymbol("[0]", DataTypeCategory.Primitive, "INT", 10),
            new StubValueSymbol("[1]", DataTypeCategory.Primitive, "INT", 20));

        var decoded = Assert.IsType<object?[]>(
            await PlcValueDecoder.DecodeAsync(new[] { 10, 20 }, symbol, CancellationToken.None));

        Assert.Equal(new object?[] { 10, 20 }, decoded);
    }

    [Fact]
    public async Task Decode_falls_back_to_raw_array_values_when_sub_symbol_count_mismatches()
    {
        // No sub-symbols at all — the decoder must still produce the raw elements
        // rather than an array of nulls.
        var symbol = new StubSymbol(DataTypeCategory.Array, "ARRAY [0..2] OF INT");

        var decoded = Assert.IsType<object?[]>(
            await PlcValueDecoder.DecodeAsync(new[] { 1, 2, 3 }, symbol, CancellationToken.None));

        Assert.Equal(new object?[] { 1, 2, 3 }, decoded);
    }

    [Fact]
    public async Task Decode_treats_struct_without_sub_symbols_as_a_pass_through()
    {
        var symbol = new StubSymbol(DataTypeCategory.Struct, "ST_Opaque");

        Assert.Equal("raw", await PlcValueDecoder.DecodeAsync("raw", symbol, CancellationToken.None));
    }

    [Fact]
    public async Task Decode_cancellation_stops_a_slow_struct_members_read_promptly()
    {
        // Directly pins Finding 1: before the fix, struct members were read via the synchronous,
        // non-cancellable ReadValue(), so a slow/blocked member read could not be interrupted by
        // the batch's CancellationToken and would run past the configured timeout. This member's
        // read never completes unless its token is cancelled; if DecodeAsync's token were ever
        // dropped on the way down to the member read, this test would hang instead of observing
        // cancellation — the WaitAsync bound below turns "token silently ignored" into a failing
        // test rather than a stuck test run.
        using var cts = new CancellationTokenSource();
        var symbol = new StubSymbol(DataTypeCategory.Struct, "ST_Motor",
            StubValueSymbol.ThatNeverCompletesRead("Speed", DataTypeCategory.Primitive, "INT"));

        var decodeTask = PlcValueDecoder.DecodeAsync(new object(), symbol, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decodeTask)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Decode_throws_AdsErrorException_when_a_struct_members_read_fails()
    {
        // Finding 1: ReadValueAsync uses the non-throwing Result pattern, so DecodeAsync must
        // check .Failed itself and throw — the same failure mode ReadValue() used to surface
        // implicitly by throwing (Beckhoff's simple/throwing overload convention).
        var symbol = new StubSymbol(DataTypeCategory.Struct, "ST_Motor",
            StubValueSymbol.ThatFailsToRead("Speed", DataTypeCategory.Primitive, "INT"));

        var ex = await Assert.ThrowsAsync<AdsErrorException>(
            () => PlcValueDecoder.DecodeAsync(new object(), symbol, CancellationToken.None));

        Assert.Equal(AdsErrorCode.DeviceError, ex.ErrorCode);
    }

    // =========================================================================
    // TryDecodeWithoutReads — the I/O-free path the synchronous ADS notification
    // handler uses (it cannot await DecodeAsync).
    // =========================================================================

    [Theory]
    [InlineData(DataTypeCategory.Primitive, "INT")]
    [InlineData(DataTypeCategory.String, "STRING(80)")]
    [InlineData(DataTypeCategory.Enum, "E_Mode")]
    public void TryDecodeWithoutReads_passes_non_container_values_through(DataTypeCategory category, string typeName)
    {
        var symbol = new StubSymbol(category, typeName);

        Assert.True(PlcValueDecoder.TryDecodeWithoutReads(42, symbol, out var decoded));
        Assert.Equal(42, decoded);
    }

    [Fact]
    public void TryDecodeWithoutReads_passes_opaque_struct_without_sub_symbols_through()
    {
        // No sub-symbols to read, so DecodeAsync passes the raw value through — no I/O needed.
        var symbol = new StubSymbol(DataTypeCategory.Struct, "ST_Opaque");

        Assert.True(PlcValueDecoder.TryDecodeWithoutReads("raw", symbol, out var decoded));
        Assert.Equal("raw", decoded);
    }

    [Fact]
    public void TryDecodeWithoutReads_returns_null_for_a_null_value()
    {
        var symbol = new StubSymbol(DataTypeCategory.Struct, "ST_Motor",
            new StubValueSymbol("Speed", DataTypeCategory.Primitive, "INT", 1500));

        // A null decodes to null on every path, container symbol or not — so even a struct
        // needs no reads for it.
        Assert.True(PlcValueDecoder.TryDecodeWithoutReads(null, symbol, out var decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public void TryDecodeWithoutReads_refuses_a_struct_with_sub_symbols()
    {
        // Decoding this reads one member per field — it must go through DecodeAsync. If this
        // stub's members were read here, StubValueSymbol.ReadValue() would throw.
        var symbol = new StubSymbol(DataTypeCategory.Struct, "ST_Motor",
            new StubValueSymbol("Speed", DataTypeCategory.Primitive, "INT", 1500));

        Assert.False(PlcValueDecoder.TryDecodeWithoutReads(new object(), symbol, out var decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public void TryDecodeWithoutReads_decodes_an_array_whose_value_carries_its_elements()
    {
        // No element sub-symbols to consult: the elements in the value are the whole answer, the
        // same object?[] DecodeAsync builds — so no read is needed and none happens.
        var symbol = new StubSymbol(DataTypeCategory.Array, "ARRAY [0..1] OF INT");

        Assert.True(PlcValueDecoder.TryDecodeWithoutReads(new[] { 10, 20 }, symbol, out var decoded));
        Assert.Equal(new object?[] { 10, 20 }, Assert.IsType<object?[]>(decoded));
    }

    // =========================================================================
    // The value first: a container read ONCE (a DynamicValue, stubbed by
    // StubStructValue) is decoded from its own members and elements. Every
    // sub-symbol below FAILS if read, so a passing test proves no read happened.
    // =========================================================================

    private static StubValueSymbol Unreadable(string name, DataTypeCategory category, string typeName,
        params ISymbol[] subSymbols) =>
        StubValueSymbol.ThatFailsToRead(name, category, typeName, subSymbols);

    [Fact]
    public async Task Decode_takes_struct_members_from_the_value_without_reading_them()
    {
        var symbol = new StubSymbol(DataTypeCategory.Struct, "ST_Motor",
            Unreadable("Speed", DataTypeCategory.Primitive, "INT"),
            Unreadable("Running", DataTypeCategory.Primitive, "BOOL"));
        var value = new StubStructValue(("Speed", (short)1500), ("Running", true));

        var decoded = Assert.IsType<Dictionary<string, object?>>(
            await PlcValueDecoder.DecodeAsync(value, symbol, CancellationToken.None));

        Assert.Equal((short)1500, decoded["Speed"]);
        Assert.Equal(true, decoded["Running"]);
        Assert.Equal(2, value.MemberLookups);
    }

    [Fact]
    public async Task Decode_walks_nested_structs_and_one_based_struct_arrays_from_the_value()
    {
        // The shape a PLC status struct arrives in: a nested struct, and an ARRAY[1..2] OF struct,
        // which Beckhoff delivers as a 1-based CLR array of DynamicValue. Indexing it from 0 would
        // throw; the decoder enumerates it.
        var unit = Unreadable("[1]", DataTypeCategory.Struct, "ST_Unit",
            Unreadable("xFault", DataTypeCategory.Primitive, "BOOL"));
        var unit2 = Unreadable("[2]", DataTypeCategory.Struct, "ST_Unit",
            Unreadable("xFault", DataTypeCategory.Primitive, "BOOL"));
        var units = Unreadable("aUnit", DataTypeCategory.Array, "ARRAY [1..2] OF ST_Unit", unit, unit2);
        var line = Unreadable("stLine", DataTypeCategory.Struct, "ST_Line",
            Unreadable("eState", DataTypeCategory.Enum, "E_State"), units);
        var status = new StubSymbol(DataTypeCategory.Struct, "ST_Status",
            Unreadable("nCycle", DataTypeCategory.Primitive, "UDINT"), line);

        var unitValues = Array.CreateInstance(typeof(object), [2], [1]);
        unitValues.SetValue(new StubStructValue(("xFault", false)), 1);
        unitValues.SetValue(new StubStructValue(("xFault", true)), 2);
        var value = new StubStructValue(
            ("nCycle", 42u),
            ("stLine", new StubStructValue(("eState", (short)6), ("aUnit", unitValues))));

        var decoded = Assert.IsType<Dictionary<string, object?>>(
            await PlcValueDecoder.DecodeAsync(value, status, CancellationToken.None));

        Assert.Equal(42u, decoded["nCycle"]);
        var decodedLine = Assert.IsType<Dictionary<string, object?>>(decoded["stLine"]);
        Assert.Equal((short)6, decodedLine["eState"]);
        var decodedUnits = Assert.IsType<object?[]>(decodedLine["aUnit"]);
        Assert.Equal([false, true], decodedUnits.Select(u => Assert.IsType<Dictionary<string, object?>>(u)["xFault"]));

        // The same value decodes identically on the synchronous, I/O-free notification path.
        Assert.True(PlcValueDecoder.TryDecodeWithoutReads(value, status, out var inline));
        Assert.Equivalent(decoded, inline, strict: true);
    }

    [Fact]
    public async Task Decode_reads_only_the_member_the_value_does_not_carry()
    {
        var symbol = new StubSymbol(DataTypeCategory.Struct, "ST_Motor",
            Unreadable("Speed", DataTypeCategory.Primitive, "INT"),
            new StubValueSymbol("Running", DataTypeCategory.Primitive, "BOOL", true));
        var value = new StubStructValue(("Speed", (short)1500));   // no "Running"

        var decoded = Assert.IsType<Dictionary<string, object?>>(
            await PlcValueDecoder.DecodeAsync(value, symbol, CancellationToken.None));

        Assert.Equal((short)1500, decoded["Speed"]);
        Assert.Equal(true, decoded["Running"]);   // read from its sub-symbol
        Assert.False(PlcValueDecoder.TryDecodeWithoutReads(value, symbol, out _));
    }

    [Fact]
    public async Task Decode_reads_each_element_when_the_array_value_is_raw_bytes()
    {
        // A raw client read of ARRAY[0..1] OF ST_Pair returns the storage as bytes: one "element"
        // per byte. Returning those as the array's elements handed the caller six bytes that look
        // like data; the element sub-symbols say what the elements really are.
        var symbol = new StubSymbol(DataTypeCategory.Array, "ARRAY [0..1] OF INT",
            new StubValueSymbol("[0]", DataTypeCategory.Primitive, "INT", (short)10),
            new StubValueSymbol("[1]", DataTypeCategory.Primitive, "INT", (short)20));

        var decoded = Assert.IsType<object?[]>(
            await PlcValueDecoder.DecodeAsync(new byte[] { 10, 0, 20, 0, 0, 0 }, symbol, CancellationToken.None));

        Assert.Equal(new object?[] { (short)10, (short)20 }, decoded);
        Assert.False(PlcValueDecoder.TryDecodeWithoutReads(new byte[] { 10, 0, 20, 0, 0, 0 }, symbol, out _));
    }
}
