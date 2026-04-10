using Microsoft.Extensions.Logging.Abstractions;

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
        await conn.WriteValuesAsync(new Dictionary<string, object> { ["X"] = 10, ["Y"] = 20 }, CancellationToken.None);
        var results = await conn.ReadValuesAsync(["X", "Y", "Z"], CancellationToken.None);

        Assert.Equal(10, results["X"]);
        Assert.Equal(20, results["Y"]);
        Assert.Null(results["Z"]);
    }

    [Fact]
    public async Task GetAdsState_ReturnsRun()
    {
        using var conn = CreateConnection();
        var state = await conn.GetAdsStateAsync(CancellationToken.None);
        Assert.Equal(global::TwinCAT.Ads.AdsState.Run, state);
    }
}
