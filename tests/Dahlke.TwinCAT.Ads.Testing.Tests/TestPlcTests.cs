using Dahlke.TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Testing.Tests;

/// <summary>
/// Pins <see cref="TestPlc"/> — the packaged answer to "a started pool with simulated
/// targets, seeded, ready to inject", which every consumer previously rebuilt out of
/// generic-host boilerplate in their own test project.
/// </summary>
public class TestPlcTests
{
    [Fact]
    public async Task Start_YieldsAPoolWithTheSeededValuesReadable()
    {
        await using var plc = await TestPlc.Create()
            .WithTarget("plc1", seed => seed["GVL.Temp"] = 21.5f)
            .StartAsync();

        var conn = plc.Pool.GetConnection("plc1");

        Assert.True(conn.IsConnected);
        Assert.Equal(21.5f, await conn.ReadValueAsync<float>("GVL.Temp"));
    }

    [Fact]
    public async Task Connection_ReturnsTheSameFacadeAsThePool()
    {
        await using var plc = await TestPlc.Create()
            .WithTarget("plc1")
            .StartAsync();

        Assert.Same(plc.Pool.GetConnection("plc1"), plc.Connection("plc1"));
    }

    [Fact]
    public async Task MultipleTargets_AreIndependentlySeeded()
    {
        await using var plc = await TestPlc.Create()
            .WithTarget("plc1", seed => seed["GVL.Temp"] = 21.5f)
            .WithTarget("plc2", seed => seed["GVL.Temp"] = 30.0f)
            .StartAsync();

        Assert.Equal(21.5f, await plc.Connection("plc1").ReadValueAsync<float>("GVL.Temp"));
        Assert.Equal(30.0f, await plc.Connection("plc2").ReadValueAsync<float>("GVL.Temp"));
    }

    [Fact]
    public async Task ConfigureTarget_ReachesTheTargetOptions()
    {
        await using var plc = await TestPlc.Create()
            .WithTarget("plc1", seed => seed["GVL.Temp"] = 21.5f)
            .ConfigureTarget("plc1", o => o.DisplayName = "Line 3")
            .StartAsync();

        Assert.Equal("Line 3", plc.Connection("plc1").DisplayName);
    }

    [Fact]
    public async Task EveryTargetIsSimulated_EvenIfConfiguredReal()
    {
        // TestPlc forces simulation, exactly as AddTwinCatAdsSimulation does. A test that
        // wants real hardware wants the hardware test project.
        await using var plc = await TestPlc.Create()
            .WithTarget("plc1")
            .ConfigureTarget("plc1", o =>
            {
                o.Mode = ConnectionMode.Real;
                o.AmsNetId = "192.168.1.10.1.1";
            })
            .StartAsync();

        Assert.Equal(ConnectionMode.Simulated, Assert.Single(plc.Pool.GetTargetStates()).Mode);
    }

    [Fact]
    public async Task RawChannels_Resolve()
    {
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();

        Assert.NotNull(plc.RawChannels);
    }

    [Fact]
    public async Task DisposeAsync_StopsThePool_AndIsIdempotent()
    {
        var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();
        var conn = plc.Connection("plc1");

        await plc.DisposeAsync();
        await plc.DisposeAsync();

        Assert.False(conn.IsConnected);
    }

    [Fact]
    public async Task UnknownTarget_Throws()
    {
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();

        Assert.Throws<UnknownPlcTargetException>(() => plc.Connection("nope"));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var builder = TestPlc.Create();

        Assert.Throws<ArgumentException>(() => builder.WithTarget("  "));
        Assert.Throws<ArgumentNullException>(
            () => builder.WithTarget("plc1", (Action<IDictionary<string, object?>>)null!));
        Assert.Throws<ArgumentNullException>(() => builder.ConfigureTarget("plc1", null!));
        Assert.Throws<ArgumentNullException>(() => builder.UseLoggerFactory(null!));
    }
}
