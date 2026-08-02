using Microsoft.Extensions.DependencyInjection;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Pins <see cref="AdsConnectionPoolBuilder"/> — the non-DI entry point for console, WPF
/// and WinForms consumers. It is a face on the DI path rather than a second wiring, so
/// these tests care most about the ways the two could differ.
/// </summary>
public class AdsConnectionPoolBuilderTests
{
    [Fact]
    public async Task SimulatedTarget_IsConnectedWhenBuildAndStartReturns()
    {
        // The pool awaits each simulated target's first connection during StartAsync, so
        // "started" means "usable" with no wait loop in the caller.
        await using var pool = await AdsConnectionPoolBuilder.Create()
            .AddTarget("plc1", o =>
            {
                o.Mode = ConnectionMode.Simulated;
                o.DisplayName = "Simulated PLC";
                o.InitialValues["GVL.Temp"] = 21.5f;
            })
            .BuildAndStartAsync();

        var conn = pool.GetConnection("plc1");

        Assert.True(conn.IsConnected);
        Assert.Equal(ConnectionState.Connected, conn.State);
        Assert.Equal("plc1", conn.PlcId);
        Assert.Equal(21.5f, await conn.ReadValueAsync<float>("GVL.Temp"));
    }

    [Fact]
    public async Task Handle_IsTheSamePoolTheProviderHolds()
    {
        // The handle delegates; it is not a second pool with its own facades.
        await using var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC")
            .BuildAndStartAsync();

        var fromProvider = pool.Services.GetRequiredService<IAdsConnectionPool>();

        Assert.Same(fromProvider.GetConnection("plc1"), pool.GetConnection("plc1"));
    }

    [Fact]
    public async Task CreateSimulation_ForcesATargetDeclaredReal()
    {
        // Mirrors AddTwinCatAdsSimulation: its whole promise is that no hardware is
        // needed, so it beats a Real declaration rather than deferring to it.
        await using var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .AddTarget("plc1", o =>
            {
                o.Mode = ConnectionMode.Real;
                o.AmsNetId = "192.168.1.10.1.1";
            })
            .BuildAndStartAsync();

        var status = Assert.Single(pool.GetTargetStates());
        Assert.Equal(ConnectionMode.Simulated, status.Mode);
        Assert.True(pool.TryGetSimulatedConnection("plc1", out _));
    }

    [Fact]
    public async Task RawChannels_Resolve()
    {
        await using var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC")
            .BuildAndStartAsync();

        Assert.NotNull(pool.RawChannels);
        Assert.Same(pool.Services.GetRequiredService<IAdsRawChannelFactory>(), pool.RawChannels);
    }

    [Fact]
    public async Task AddTarget_CalledTwiceForOneId_ConfiguresOneTarget()
    {
        // AddTarget configures a target; it does not replace one. Two calls compose,
        // which is what makes a shared helper method that adds defaults usable.
        await using var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC")
            .AddTarget("plc1", o => o.InitialValues["GVL.Temp"] = 21.5f)
            .BuildAndStartAsync();

        var conn = pool.GetConnection("plc1");
        Assert.Equal("Simulated PLC", conn.DisplayName);
        Assert.Equal(21.5f, await conn.ReadValueAsync<float>("GVL.Temp"));
    }

    [Fact]
    public async Task DisposeAsync_StopsThePool_AndIsIdempotent()
    {
        var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC")
            .BuildAndStartAsync();

        var conn = pool.GetConnection("plc1");
        Assert.True(conn.IsConnected);

        await pool.DisposeAsync();
        await pool.DisposeAsync();   // second call must be a no-op, not a throw

        Assert.False(conn.IsConnected);
    }

    [Fact]
    public async Task UnknownTarget_ThrowsListingTheConfiguredIds()
    {
        await using var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC")
            .BuildAndStartAsync();

        var ex = Assert.Throws<UnknownPlcTargetException>(() => pool.GetConnection("nope"));
        Assert.Contains("plc1", ex.Message);
        Assert.False(pool.TryGetConnection("nope", out _));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var builder = AdsConnectionPoolBuilder.Create();

        Assert.Throws<ArgumentNullException>(() => builder.Configure(null!));
        Assert.Throws<ArgumentNullException>(() => builder.UseConfiguration(null!));
        Assert.Throws<ArgumentNullException>(() => builder.UseLoggerFactory(null!));
        Assert.Throws<ArgumentNullException>(() => builder.ConfigureServices(null!));
        Assert.Throws<ArgumentNullException>(() => builder.AddTarget("plc1", null!));
        Assert.Throws<ArgumentException>(() => builder.AddTarget("  ", o => { }));
    }
}
