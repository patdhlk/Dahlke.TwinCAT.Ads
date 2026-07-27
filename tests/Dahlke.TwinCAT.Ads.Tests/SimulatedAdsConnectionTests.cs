using Microsoft.Extensions.Logging.Abstractions;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Tests;

public class SimulatedAdsConnectionTests
{
    private SimulatedAdsConnection CreateConnection()
        => new("test-plc", "Test PLC", NullLoggerFactory.Instance);

    [Fact]
    public void IsConnected_ReturnsTrue()
    {
        using var conn = CreateConnection();
        Assert.True(conn.IsConnected);
    }

    [Fact]
    public async Task WriteAndRead_RoundTrips()
    {
        using var conn = CreateConnection();
        await conn.WriteValueAsync("MySymbol", 42, CancellationToken.None);
        var result = await conn.ReadValueAsync("MySymbol", CancellationToken.None);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task ReadValue_UnknownSymbol_ReturnsNull()
    {
        using var conn = CreateConnection();
        var result = await conn.ReadValueAsync("DoesNotExist", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task SetInitialValues_AreReadable()
    {
        using var conn = CreateConnection();
        conn.SetInitialValues(new Dictionary<string, object?> { ["A"] = 1, ["B"] = "hello" });

        Assert.Equal(1, await conn.ReadValueAsync("A", CancellationToken.None));
        Assert.Equal("hello", await conn.ReadValueAsync("B", CancellationToken.None));
    }

    [Fact]
    public async Task ReadWriteValues_BatchOperations()
    {
        using var conn = CreateConnection();
        await conn.WriteValuesAsync(new Dictionary<string, object?> { ["X"] = 10, ["Y"] = 20 }, CancellationToken.None);
        var results = await conn.ReadValuesAsync(["X", "Y", "Z"], CancellationToken.None);

        Assert.Equal(10, results["X"].Value);
        Assert.Equal(20, results["Y"].Value);
        Assert.Null(results["Z"].Value);
    }

    [Fact]
    public async Task GetAdsState_ReturnsRun()
    {
        using var conn = CreateConnection();
        var state = await conn.GetAdsStateAsync(CancellationToken.None);
        Assert.Equal(global::TwinCAT.Ads.AdsState.Run, state);
    }

    [Fact]
    public async Task ReadValueWithMetadataAsync_ReturnsStoredValueAndInferredMetadata()
    {
        using var conn = CreateConnection();
        await conn.WriteValueAsync("MAIN.Speed", 1500, CancellationToken.None);

        var result = await conn.ReadValueWithMetadataAsync("MAIN.Speed", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1500, result.Value);
        Assert.Equal("MAIN.Speed", result.SymbolPath);
        Assert.Equal("DINT", result.TypeName);
        Assert.Equal("Primitive", result.Category);
    }

    [Fact]
    public async Task ReadValueWithMetadataAsync_UnknownSymbol_ThrowsAdsErrorException_SymbolNotFound()
    {
        using var conn = CreateConnection();

        var ex = await Assert.ThrowsAsync<AdsErrorException>(
            () => conn.ReadValueWithMetadataAsync("DoesNotExist", CancellationToken.None));

        Assert.Equal(AdsErrorCode.DeviceSymbolNotFound, ex.ErrorCode);
    }

    [Theory]
    [InlineData(true, "BOOL", "Primitive")]
    [InlineData((sbyte)1, "SINT", "Primitive")]
    [InlineData((byte)1, "USINT", "Primitive")]
    [InlineData((short)1, "INT", "Primitive")]
    [InlineData((ushort)1, "UINT", "Primitive")]
    [InlineData(1, "DINT", "Primitive")]
    [InlineData(1u, "UDINT", "Primitive")]
    [InlineData(1L, "LINT", "Primitive")]
    [InlineData(1ul, "ULINT", "Primitive")]
    [InlineData(1f, "REAL", "Primitive")]
    [InlineData(1d, "LREAL", "Primitive")]
    [InlineData("hello", "STRING", "String")]
    public async Task ReadValueWithMetadataAsync_InfersExpectedTypeNameAndCategory(
        object value, string expectedTypeName, string expectedCategory)
    {
        using var conn = CreateConnection();
        await conn.WriteValueAsync("MAIN.Value", value, CancellationToken.None);

        var result = await conn.ReadValueWithMetadataAsync("MAIN.Value", CancellationToken.None);

        Assert.Equal(expectedTypeName, result.TypeName);
        Assert.Equal(expectedCategory, result.Category);
    }

    [Fact]
    public async Task ReadValueWithMetadataAsync_StructLikeDictionary_InfersStructCategory()
    {
        using var conn = CreateConnection();
        await conn.WriteValueAsync(
            "MAIN.Motor",
            new Dictionary<string, object?> { ["Speed"] = 1500 },
            CancellationToken.None);

        var result = await conn.ReadValueWithMetadataAsync("MAIN.Motor", CancellationToken.None);

        Assert.Equal("STRUCT", result.TypeName);
        Assert.Equal("Struct", result.Category);
    }

    [Fact]
    public async Task ReadValueWithMetadataAsync_ArrayValue_InfersArrayCategory()
    {
        using var conn = CreateConnection();
        await conn.WriteValueAsync("MAIN.Values", new[] { 1, 2, 3 }, CancellationToken.None);

        var result = await conn.ReadValueWithMetadataAsync("MAIN.Values", CancellationToken.None);

        Assert.Equal("ARRAY", result.TypeName);
        Assert.Equal("Array", result.Category);
    }
}
