using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
    public async Task ConfigureServices_HostedService_StartsAfterThePoolIsAlreadyConnected()
    {
        // A ConfigureServices delegate runs BEFORE AddTwinCatAds... internally (so its
        // TryAdd defaults, e.g. a custom ILoggerFactory, can win) — but any IHostedService
        // it registers must still START after router/pool/raw-channels, exactly as a
        // companion package's hosted service (e.g. AddTwinCatAdsAlarms's monitor) would on
        // a generic host via AddTwinCatAds(...).AddTwinCatAdsAlarms(...). Registration
        // order and start order are not the same axis, and this pins that they aren't.
        await using var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC")
            .ConfigureServices(services => services.AddHostedService<ConnectedAtStartProbe>())
            .BuildAndStartAsync();

        var probe = pool.Services.GetServices<IHostedService>().OfType<ConnectedAtStartProbe>().Single();
        Assert.True(probe.WasConnectedAtStart);
    }

    /// <summary>
    /// Records, at <see cref="StartAsync"/> time, whether the pool it depends on already
    /// reports its target as connected — the probe for
    /// <see cref="ConfigureServices_HostedService_StartsAfterThePoolIsAlreadyConnected"/>.
    /// </summary>
    private sealed class ConnectedAtStartProbe(IAdsConnectionPool pool) : IHostedService
    {
        public bool WasConnectedAtStart { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            WasConnectedAtStart = pool.GetConnection("plc1").IsConnected;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
