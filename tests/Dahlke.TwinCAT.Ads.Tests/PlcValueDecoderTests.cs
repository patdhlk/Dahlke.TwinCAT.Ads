using TwinCAT.TypeSystem;

namespace Dahlke.TwinCAT.Ads.Tests;

public class PlcValueDecoderTests
{
    [Fact]
    public void Decode_returns_null_for_null_value()
    {
        var symbol = new StubSymbol(DataTypeCategory.Primitive, "INT");

        Assert.Null(PlcValueDecoder.Decode(null, symbol));
    }

    [Fact]
    public void Decode_passes_primitives_through_unchanged()
    {
        var symbol = new StubSymbol(DataTypeCategory.Primitive, "INT");

        Assert.Equal(42, PlcValueDecoder.Decode(42, symbol));
    }

    [Fact]
    public void Decode_passes_strings_through_unchanged()
    {
        var symbol = new StubSymbol(DataTypeCategory.String, "STRING(80)");

        Assert.Equal("hello", PlcValueDecoder.Decode("hello", symbol));
    }

    [Fact]
    public void Decode_passes_enums_through_as_backing_value()
    {
        var symbol = new StubSymbol(DataTypeCategory.Enum, "E_Mode");

        Assert.Equal(2, PlcValueDecoder.Decode(2, symbol));
    }

    [Fact]
    public void Decode_converts_struct_to_dictionary_keyed_by_instance_name()
    {
        var symbol = new StubSymbol(DataTypeCategory.Struct, "ST_Motor",
            new StubValueSymbol("Speed", DataTypeCategory.Primitive, "INT", 1500),
            new StubValueSymbol("Running", DataTypeCategory.Primitive, "BOOL", true));

        var decoded = Assert.IsType<Dictionary<string, object?>>(
            PlcValueDecoder.Decode(new object(), symbol));

        Assert.Equal(2, decoded.Count);
        Assert.Equal(1500, decoded["Speed"]);
        Assert.Equal(true, decoded["Running"]);
    }

    [Fact]
    public void Decode_converts_nested_struct_recursively()
    {
        var inner = new StubValueSymbol("Inner", DataTypeCategory.Struct, "ST_Inner", new object(),
            new StubValueSymbol("Depth", DataTypeCategory.Primitive, "INT", 7));
        var outer = new StubSymbol(DataTypeCategory.Struct, "ST_Outer", inner);

        var decoded = Assert.IsType<Dictionary<string, object?>>(
            PlcValueDecoder.Decode(new object(), outer));
        var nested = Assert.IsType<Dictionary<string, object?>>(decoded["Inner"]);

        Assert.Equal(7, nested["Depth"]);
    }

    [Fact]
    public void Decode_converts_array_using_sub_symbols_when_counts_match()
    {
        var symbol = new StubSymbol(DataTypeCategory.Array, "ARRAY [0..1] OF INT",
            new StubValueSymbol("[0]", DataTypeCategory.Primitive, "INT", 10),
            new StubValueSymbol("[1]", DataTypeCategory.Primitive, "INT", 20));

        var decoded = Assert.IsType<object?[]>(
            PlcValueDecoder.Decode(new[] { 10, 20 }, symbol));

        Assert.Equal(new object?[] { 10, 20 }, decoded);
    }

    [Fact]
    public void Decode_falls_back_to_raw_array_values_when_sub_symbol_count_mismatches()
    {
        // No sub-symbols at all — the decoder must still produce the raw elements
        // rather than an array of nulls.
        var symbol = new StubSymbol(DataTypeCategory.Array, "ARRAY [0..2] OF INT");

        var decoded = Assert.IsType<object?[]>(
            PlcValueDecoder.Decode(new[] { 1, 2, 3 }, symbol));

        Assert.Equal(new object?[] { 1, 2, 3 }, decoded);
    }

    [Fact]
    public void Decode_treats_struct_without_sub_symbols_as_a_pass_through()
    {
        var symbol = new StubSymbol(DataTypeCategory.Struct, "ST_Opaque");

        Assert.Equal("raw", PlcValueDecoder.Decode("raw", symbol));
    }
}
