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
    public void TryDecodeWithoutReads_refuses_an_array()
    {
        // An array is rebuilt element by element (object?[]), never passed through, so it is
        // refused even when its elements would not need sub-symbol reads.
        var symbol = new StubSymbol(DataTypeCategory.Array, "ARRAY [0..1] OF INT");

        Assert.False(PlcValueDecoder.TryDecodeWithoutReads(new[] { 10, 20 }, symbol, out var decoded));
        Assert.Null(decoded);
    }
}
