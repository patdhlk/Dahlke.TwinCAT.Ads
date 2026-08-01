using Microsoft.Extensions.Logging.Abstractions;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// <see cref="PlcSymbol{T}"/> and <see cref="PlcSymbolExtensions"/> — a symbol's path and its
/// .NET type declared once, instead of repeated as a string literal and a type argument at every
/// call site.
///
/// Coverage:
/// - Round-trips through the handle behave exactly as the string overloads do, because that is
///   all the extensions do.
/// - The handle's type is what the value is read and written as, including the widening the
///   string overload already performs.
/// - Batch results, which are keyed by path and untyped, can be read back through a handle.
/// - The unusable `default` struct value is rejected with a message naming the mistake, rather
///   than reaching the ADS layer as a null path.
/// - Handles are values: equal by path and type, and free to build.
/// </summary>
public class PlcSymbolTests
{
    private static class Symbols
    {
        public static readonly PlcSymbol<float> Setpoint = new("GVL.Setpoint");
        public static readonly PlcSymbol<bool> Pump = new("GVL.PumpRunning");
        public static readonly PlcSymbol<int> Counter = new("GVL.Counter");
    }

    private static SimulatedAdsConnection CreateSim()
        => new("test-plc", "Test PLC", NullLoggerFactory.Instance);

    // =========================================================================
    // Round-trips
    // =========================================================================

    [Fact]
    public async Task WriteThenRead_ThroughHandles_RoundTripsWithNoPathLiteralOrTypeArgument()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;

        await conn.WriteValueAsync(Symbols.Setpoint, 21.5f);
        await conn.WriteValueAsync(Symbols.Pump, true);

        Assert.Equal(21.5f, await conn.ReadValueAsync(Symbols.Setpoint));
        Assert.True(await conn.ReadValueAsync(Symbols.Pump));
    }

    [Fact]
    public async Task HandleAndStringOverload_AddressTheSameSymbol()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;

        await conn.WriteValueAsync(Symbols.Counter, 42);

        // Written through the handle, read back through the path: one symbol, not two.
        Assert.Equal(42, await conn.ReadValueAsync<int>("GVL.Counter"));
    }

    [Fact]
    public async Task ReadValueWithMetadata_ThroughHandle_ReportsThePlcType()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;
        await conn.WriteValueAsync(Symbols.Counter, 42);

        var result = await conn.ReadValueWithMetadataAsync(Symbols.Counter);

        Assert.True(result.Succeeded);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public async Task Subscribe_ThroughHandle_DeliversTheHandlesType()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;
        var seen = new List<float>();

        using var sub = await conn.SubscribeAsync(Symbols.Setpoint, 100, (_, v) => seen.Add(v));
        await conn.WriteValueAsync(Symbols.Setpoint, 23.5f);

        Assert.Equal([23.5f], seen);
    }

    [Fact]
    public async Task ReadValueAsync_ThroughHandle_AppliesTheSameWideningAsTheStringOverload()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;
        await conn.WriteValueAsync<int>("GVL.Speed", 1500);

        // The handle binds the type; conversion is still the string overload's.
        var asDouble = new PlcSymbol<double>("GVL.Speed");
        Assert.Equal(1500d, await conn.ReadValueAsync(asDouble));
    }

    // =========================================================================
    // Batch results
    // =========================================================================

    [Fact]
    public async Task GetValue_ReadsABatchResultBackThroughItsHandle()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;
        await conn.WriteValueAsync(Symbols.Setpoint, 21.5f);
        await conn.WriteValueAsync(Symbols.Pump, true);

        var results = await conn.ReadValuesAsync([Symbols.Setpoint.Path, Symbols.Pump.Path]);

        Assert.Equal(21.5f, results.GetValue(Symbols.Setpoint));
        Assert.True(results.GetValue(Symbols.Pump));
    }

    [Fact]
    public async Task GetValue_ForASymbolNotInTheBatch_SaysSo()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;
        await conn.WriteValueAsync(Symbols.Setpoint, 21.5f);

        var results = await conn.ReadValuesAsync([Symbols.Setpoint.Path]);

        var ex = Assert.Throws<KeyNotFoundException>(() => results.GetValue(Symbols.Pump));
        Assert.Contains("GVL.PumpRunning", ex.Message);
    }

    // =========================================================================
    // The default value is rejected at the boundary
    // =========================================================================

    [Fact]
    public void Constructor_RejectsABlankPath()
    {
        Assert.Throws<ArgumentException>(() => new PlcSymbol<int>(""));
        Assert.Throws<ArgumentException>(() => new PlcSymbol<int>("   "));

        // Null is the ArgumentNullException subclass, per ThrowIfNullOrWhiteSpace.
        Assert.Throws<ArgumentNullException>(() => new PlcSymbol<int>(null!));
    }

    [Fact]
    public void DefaultValue_IsReportedAsEmpty()
    {
        PlcSymbol<int> uninitialised = default;

        Assert.True(uninitialised.IsEmpty);
        Assert.False(new PlcSymbol<int>("MAIN.X").IsEmpty);
    }

    [Fact]
    public async Task DefaultValue_IsRejectedWithAMessageNamingTheMistake()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;
        PlcSymbol<int> uninitialised = default;

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => conn.ReadValueAsync(uninitialised));

        // Not a DeviceSymbolNotFound from a null path reaching the ADS layer.
        Assert.Contains("default value", ex.Message);
        Assert.Contains("new PlcSymbol<Int32>", ex.Message);
    }

    [Fact]
    public async Task DefaultValue_IsRejectedOnEveryOperation()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;
        PlcSymbol<int> bad = default;

        await Assert.ThrowsAsync<ArgumentException>(() => conn.ReadValueAsync(bad));
        await Assert.ThrowsAsync<ArgumentException>(() => conn.WriteValueAsync(bad, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => conn.ReadValueWithMetadataAsync(bad));
        await Assert.ThrowsAsync<ArgumentException>(() => conn.SubscribeAsync(bad, 100, (_, _) => { }));
        Assert.Throws<ArgumentException>(
            () => new Dictionary<string, AdsValueResult>().GetValue(bad));
    }

    // =========================================================================
    // Handles are values
    // =========================================================================

    [Fact]
    public void Handles_AreEqualByPathAndType()
    {
        Assert.Equal(new PlcSymbol<int>("MAIN.X"), new PlcSymbol<int>("MAIN.X"));
        Assert.NotEqual(new PlcSymbol<int>("MAIN.X"), new PlcSymbol<int>("MAIN.Y"));

        // Case matters here even though PLC symbol lookup is case-insensitive: this is ordinary
        // record equality over the declared path, not a symbol-resolution rule.
        Assert.NotEqual(new PlcSymbol<int>("MAIN.X"), new PlcSymbol<int>("main.x"));
    }

    [Fact]
    public void ToString_IsThePath_SoAHandleLogsAsItsSymbol()
    {
        Assert.Equal("GVL.Setpoint", Symbols.Setpoint.ToString());
        Assert.Equal("reading GVL.Setpoint", $"reading {Symbols.Setpoint}");
        Assert.Equal("", default(PlcSymbol<int>).ToString());
    }

    // =========================================================================
    // Composition with the other call-site features
    // =========================================================================

    [Fact]
    public async Task Handles_ComposeWithWithTimeout()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;
        await conn.WriteValueAsync(Symbols.Counter, 7);

        // The extensions extend IAdsConnection, which the scoped view also implements.
        var scoped = conn.WithTimeout(TimeSpan.FromSeconds(30));

        Assert.Equal(7, await scoped.ReadValueAsync(Symbols.Counter));
    }
}
