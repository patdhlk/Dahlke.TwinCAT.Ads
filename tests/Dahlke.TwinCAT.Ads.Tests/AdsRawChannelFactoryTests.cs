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

    [Fact]
    public void Get_IsCaseInsensitiveOnNetId_ButNotAcrossPorts()
    {
        using var factory = Create(new FakeTimeProvider());

        Assert.Same(factory.Get("1.2.3.4.5.6", 851), factory.Get("1.2.3.4.5.6", 851));
        Assert.NotSame(factory.Get("1.2.3.4.5.6", 851), factory.Get("1.2.3.4.5.6", 852));
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

    [Fact]
    public void ActiveChannel_IsNotEvicted()
    {
        var clock = new FakeTimeProvider();
        using var factory = Create(clock, o => o.IdleEvictionMs = 10_000);

        var channel = factory.Get("1.2.3.4.5.6", 0xFFFF);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        factory.SweepOnce();

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
