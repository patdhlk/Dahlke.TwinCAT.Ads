using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

public class AdsRawChannelFactoryTests
{
    private static AdsRawChannelFactory Create(
        FakeTimeProvider clock, Action<AdsRawChannelOptions>? configure = null)
    {
        var options = new TwinCatAdsOptions();
        options.RawChannels.Mode = ConnectionMode.Simulated;
        configure?.Invoke(options.RawChannels);

        return new AdsRawChannelFactory(
            Options.Create(options), NullLoggerFactory.Instance, clock);
    }

    [Fact]
    public void Get_ReturnsTheSameInstanceForTheSameKey()
    {
        using var factory = Create(new FakeTimeProvider());

        var first = factory.Get("1.2.3.4.5.6", 0xFFFF);
        var second = factory.Get("1.2.3.4.5.6", 0xFFFF);

        Assert.Same(first, second);
        Assert.Equal(1, factory.ChannelCount);
    }

    /// <summary>
    /// Pins <c>ChannelKeyComparer</c> on both axes. The Net IDs differ only in
    /// case: <see cref="IAdsRawChannelFactory.Get"/> is total and validates
    /// nothing, so a caller really can hand it a Net ID in any case and must not
    /// get back a second channel for the same target.
    /// </summary>
    [Fact]
    public void Get_IsCaseInsensitiveOnNetId_ButNotAcrossPorts()
    {
        using var factory = Create(new FakeTimeProvider());

        Assert.Same(factory.Get("aB.2.3.4.5.6", 851), factory.Get("Ab.2.3.4.5.6", 851));
        Assert.NotSame(factory.Get("aB.2.3.4.5.6", 851), factory.Get("aB.2.3.4.5.6", 852));
        Assert.Equal(2, factory.ChannelCount);
    }

    [Fact]
    public void Get_IsTotal_ForAnUnreachableTarget()
    {
        using var factory = Create(new FakeTimeProvider());

        var channel = factory.Get("99.99.99.99.1.1", 12345);

        Assert.NotNull(channel);
        Assert.Equal(ConnectionState.Disconnected, channel.State);
    }

    [Fact]
    public void ConcurrentGet_YieldsExactlyOneChannel()
    {
        using var factory = Create(new FakeTimeProvider());

        var results = new IAdsRawChannel[64];
        Parallel.For(0, results.Length, i => results[i] = factory.Get("1.2.3.4.5.6", 0xFFFF));

        Assert.All(results, c => Assert.Same(results[0], c));
        Assert.Equal(1, factory.ChannelCount);
    }

    [Fact]
    public async Task IdleChannel_HasItsTransportEvicted_ButKeepsItsIdentity()
    {
        var clock = new FakeTimeProvider();
        using var factory = Create(clock, o => o.IdleEvictionMs = 1000);
        await factory.StartAsync(CancellationToken.None);

        var channel = factory.Get("1.2.3.4.5.6", 0xFFFF);
        factory.TryGetSimulated("1.2.3.4.5.6", 0xFFFF, out var sim);
        sim!.Seed(0x11, 1, [1]);
        await channel.ReadAsync(0x11, 1, new byte[1], CancellationToken.None);
        Assert.Equal(ConnectionState.Connected, channel.State);

        clock.Advance(TimeSpan.FromMilliseconds(1500));
        factory.SweepOnce();

        Assert.Equal(ConnectionState.Disconnected, channel.State);
        Assert.Same(channel, factory.Get("1.2.3.4.5.6", 0xFFFF));   // identity survives

        await factory.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EvictedChannel_ReconnectsTransparentlyOnNextOperation()
    {
        var clock = new FakeTimeProvider();
        using var factory = Create(clock, o => o.IdleEvictionMs = 1000);

        var channel = factory.Get("1.2.3.4.5.6", 0xFFFF);
        factory.TryGetSimulated("1.2.3.4.5.6", 0xFFFF, out var sim);
        sim!.Seed(0x11, 1, [42]);

        await channel.ReadAsync(0x11, 1, new byte[1], CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        factory.SweepOnce();

        var buffer = new byte[1];
        await channel.ReadAsync(0x11, 1, buffer, CancellationToken.None);

        Assert.Equal(ConnectionState.Connected, channel.State);
        Assert.Equal(42, buffer[0]);   // the simulated store outlives the transport
    }

    /// <summary>
    /// Drives two channels to a live transport apiece.
    /// </summary>
    private static async Task<(IAdsRawChannel First, IAdsRawChannel Second)> ConnectTwoAsync(
        AdsRawChannelFactory factory)
    {
        var first = factory.Get("1.2.3.4.5.6", 851);
        var second = factory.Get("1.2.3.4.5.6", 852);

        foreach (var (netId, port) in new[] { ("1.2.3.4.5.6", 851), ("1.2.3.4.5.6", 852) })
        {
            factory.TryGetSimulated(netId, port, out var sim);
            sim!.Seed(0x11, 1, [1]);
        }

        await first.ReadAsync(0x11, 1, new byte[1], CancellationToken.None);
        await second.ReadAsync(0x11, 1, new byte[1], CancellationToken.None);

        Assert.Equal(ConnectionState.Connected, first.State);
        Assert.Equal(ConnectionState.Connected, second.State);
        return (first, second);
    }

    /// <summary>
    /// <see cref="IAdsRawChannelFactory"/> documents that it "owns every underlying
    /// transport and releases them at host shutdown". This pins the
    /// <see cref="IHostedService"/> half of that promise.
    /// </summary>
    /// <remarks>
    /// Unasserted, a consumer that builds and disposes a service provider per
    /// integration test leaks one live <c>AdsClient</c> per addressed target,
    /// silently. Replacing the <c>Shutdown</c> loop with a no-op must fail here.
    /// </remarks>
    [Fact]
    public async Task StopAsync_ReleasesEveryLiveTransport()
    {
        var clock = new FakeTimeProvider();
        using var factory = Create(clock);
        await factory.StartAsync(CancellationToken.None);

        var (first, second) = await ConnectTwoAsync(factory);

        await factory.StopAsync(CancellationToken.None);

        Assert.Equal(ConnectionState.Disconnected, first.State);
        Assert.Equal(ConnectionState.Disconnected, second.State);
    }

    /// <summary>
    /// The <see cref="IDisposable"/> half of the same promise — the path the DI
    /// container takes, and the one that runs when a host is disposed without ever
    /// being stopped.
    /// </summary>
    [Fact]
    public async Task Dispose_ReleasesEveryLiveTransport()
    {
        var clock = new FakeTimeProvider();
        using var factory = Create(clock);

        var (first, second) = await ConnectTwoAsync(factory);

        factory.Dispose();   // the `using` disposes again: teardown is idempotent

        Assert.Equal(ConnectionState.Disconnected, first.State);
        Assert.Equal(ConnectionState.Disconnected, second.State);
    }

    /// <summary>
    /// The idle WINDOW itself: a channel used 500 ms ago, with a 10 s window, must
    /// keep its transport across a sweep.
    /// </summary>
    /// <remarks>
    /// A live transport has to exist first. Without an operation, <c>Get</c> alone
    /// creates none, <c>TryEvictIfIdle</c> short-circuits at <c>stale is null</c>
    /// before it ever consults the clock, and the assertion measures nothing —
    /// <see cref="ConnectionState.Disconnected"/> before and after. Deleting the
    /// <c>LastUseUtc</c> check from <c>AdsRawChannel.TryEvictIfIdle</c> must make
    /// this test fail; that mutant is otherwise green across the whole suite and
    /// would silently make <see cref="AdsRawChannelOptions.IdleEvictionMs"/>
    /// decorative in production.
    /// </remarks>
    [Fact]
    public async Task ActiveChannel_IsNotEvicted()
    {
        var clock = new FakeTimeProvider();
        using var factory = Create(clock, o => o.IdleEvictionMs = 10_000);

        var channel = factory.Get("1.2.3.4.5.6", 0xFFFF);
        factory.TryGetSimulated("1.2.3.4.5.6", 0xFFFF, out var sim);
        sim!.Seed(0x11, 1, [7]);
        await channel.ReadAsync(0x11, 1, new byte[1], CancellationToken.None);
        Assert.Equal(ConnectionState.Connected, channel.State);

        clock.Advance(TimeSpan.FromMilliseconds(500));   // well inside the 10 s window
        factory.SweepOnce();

        Assert.Equal(ConnectionState.Connected, channel.State);
        Assert.Same(channel, factory.Get("1.2.3.4.5.6", 0xFFFF));
    }

    [Fact]
    public void TryGetSimulated_ReturnsFalse_ForARealFactory()
    {
        var options = new TwinCatAdsOptions();
        options.RawChannels.Mode = ConnectionMode.Real;
        using var factory = new AdsRawChannelFactory(
            Options.Create(options), NullLoggerFactory.Instance, new FakeTimeProvider());

        Assert.False(factory.TryGetSimulated("1.2.3.4.5.6", 851, out var sim));
        Assert.Null(sim);
    }

    [Fact]
    public async Task ConfiguredSeed_IsMaterialisedOnFirstUse()
    {
        var clock = new FakeTimeProvider();
        using var factory = Create(clock, o =>
            o.Seed["1.2.3.4.5.6:0xFFFF"] = new() { ["0x11:1001"] = "02000000" });

        var channel = factory.Get("1.2.3.4.5.6", 0xFFFF);
        var buffer = new byte[4];
        var read = await channel.ReadAsync(0x11, 1001, buffer, CancellationToken.None);

        Assert.Equal(4, read);
        Assert.Equal([0x02, 0x00, 0x00, 0x00], buffer);
    }

    [Fact]
    public async Task Registration_ResolvesTheFactoryAsASingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(o =>
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6", Port = 851 });

        await using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IAdsRawChannelFactory>();
        var second = provider.GetRequiredService<IAdsRawChannelFactory>();

        Assert.Same(first, second);
        Assert.Same(first.Get("1.2.3.4.5.6", 851), second.Get("1.2.3.4.5.6", 851));
    }
}
