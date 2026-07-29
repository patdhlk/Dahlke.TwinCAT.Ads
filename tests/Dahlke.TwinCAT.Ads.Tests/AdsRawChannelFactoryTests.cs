using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

public class AdsRawChannelFactoryTests
{
    private static AdsRawChannelFactory Create(
        FakeTimeProvider clock,
        Action<AdsRawChannelOptions>? configure = null,
        ILoggerFactory? loggerFactory = null)
    {
        var options = new TwinCatAdsOptions();
        options.RawChannels.Mode = ConnectionMode.Simulated;
        configure?.Invoke(options.RawChannels);

        return new AdsRawChannelFactory(
            Options.Create(options), loggerFactory ?? NullLoggerFactory.Instance, clock);
    }

    /// <summary>Captures warning-level messages so a log assertion is possible.</summary>
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public List<string> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Warnings);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class RecordingLogger : ILogger
        {
            private readonly List<string> _warnings;

            public RecordingLogger(List<string> warnings) => _warnings = warnings;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel != LogLevel.Warning)
                    return;

                lock (_warnings)
                    _warnings.Add(formatter(state, exception));
            }
        }
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

    /// <summary>
    /// Every spelling of one physical device must land on ONE channel.
    /// </summary>
    /// <remarks>
    /// Keying on the caller's raw string made <c>"1.2.3.4.5.6"</c>,
    /// <c>"01.2.3.4.5.6"</c> and <c>" 1.2.3.4.5.6"</c> three channels addressing
    /// one target. <c>AmsNetId.TryParse</c> canonicalises the leading zero but does
    /// NOT trim, so the trim has to happen first — both halves are asserted here.
    /// </remarks>
    [Theory]
    [InlineData("01.2.3.4.5.6")]        // canonicalised by AmsNetId
    [InlineData(" 1.2.3.4.5.6")]        // leading whitespace: AmsNetId won't parse it
    [InlineData("1.2.3.4.5.6 ")]
    [InlineData("001.002.3.4.5.6")]
    public void Get_NormalisesTheNetId_SoOneDeviceIsOneChannel(string spelling)
    {
        using var factory = Create(new FakeTimeProvider());

        var canonical = factory.Get("1.2.3.4.5.6", 851);

        Assert.Same(canonical, factory.Get(spelling, 851));
        Assert.Equal(1, factory.ChannelCount);
    }

    /// <summary>
    /// The consequence that actually bites: a seed applied through one spelling
    /// must be readable through another, or seeding "silently doesn't work".
    /// </summary>
    [Fact]
    public async Task SeedThroughOneSpelling_IsReadableThroughAnother()
    {
        using var factory = Create(new FakeTimeProvider());

        Assert.True(factory.TryGetSimulated("01.2.3.4.5.6", 851, out var sim));
        sim.Seed(0x11, 1, [99]);

        var channel = factory.Get("1.2.3.4.5.6", 851);
        var buffer = new byte[1];
        await channel.ReadAsync(0x11, 1, buffer, CancellationToken.None);

        Assert.Equal(99, buffer[0]);
    }

    /// <summary>
    /// A configured seed key must reach the channel it names regardless of how
    /// either side spells the Net ID — <c>Get</c> normalises, so the seed lookup
    /// has to normalise too or the two never meet.
    /// </summary>
    [Fact]
    public async Task ConfiguredSeed_MatchesAcrossSpellings()
    {
        using var factory = Create(new FakeTimeProvider(), o =>
            o.Seed["01.2.3.4.5.6:851"] = new() { ["0x11:1001"] = "7B" });

        var channel = factory.Get("1.2.3.4.5.6", 851);
        var buffer = new byte[1];
        var read = await channel.ReadAsync(0x11, 1001, buffer, CancellationToken.None);

        Assert.Equal(1, read);
        Assert.Equal(0x7B, buffer[0]);
    }

    /// <summary>
    /// An out-of-range octet is silently zeroed by the ADS stack, so the channel
    /// addresses a different device than the text suggests. <c>Get</c> is total and
    /// cannot reject it — but it must not stay silent about it either.
    /// </summary>
    /// <remarks>
    /// Deduped per SPELLING, so a polling caller cannot flood the log. The benign
    /// half is asserted too: a well-formed ID, and a merely non-canonical one such
    /// as <c>"01.2.3.4.5.6"</c>, must NOT warn — otherwise the warning would be
    /// noise rather than signal.
    /// </remarks>
    [Fact]
    public void Get_WarnsOncePerSpelling_WhenAnOctetIsLaundered()
    {
        var logs = new RecordingLoggerFactory();
        using var factory = Create(new FakeTimeProvider(), loggerFactory: logs);

        var channel = factory.Get("999.1.1.1.1.1", 851);

        // 999 is ZEROED, not reduced modulo 256 — so this really is 0.1.1.1.1.1.
        Assert.Equal("0.1.1.1.1.1", channel.AmsNetId);
        Assert.Same(channel, factory.Get("0.1.1.1.1.1", 851));

        var warning = Assert.Single(logs.Warnings);
        Assert.Contains("999.1.1.1.1.1", warning);
        Assert.Contains("0.1.1.1.1.1", warning);

        factory.Get("999.1.1.1.1.1", 851);      // repeat lookup: no second warning
        factory.Get("1.2.3.4.5.6", 851);        // well-formed: never warns
        factory.Get("01.2.3.4.5.6", 852);       // non-canonical but in range: never warns

        Assert.Single(logs.Warnings);

        // A DIFFERENT laundered spelling of the SAME device warns on its own
        // account — dedupe is per spelling, not per channel. (256 zeroes too.)
        factory.Get("256.1.1.1.1.1", 851);

        Assert.Equal(2, logs.Warnings.Count);
        Assert.Contains(logs.Warnings, w => w.Contains("256.1.1.1.1.1"));
    }

    /// <summary>
    /// The ordering a create-time warning could never catch: the canonical
    /// spelling is requested first, so by the time the malformed one arrives the
    /// channel already exists and <c>GetOrAdd</c>'s factory delegate never runs.
    /// </summary>
    /// <remarks>
    /// This is the motivating case for the warning existing at all — a caller with
    /// both spellings in play, wondering why two "different" targets share state.
    /// A diagnostic that goes quiet exactly there is not doing its job.
    /// </remarks>
    [Fact]
    public void Get_WarnsAboutLaundering_EvenWhenTheChannelAlreadyExists()
    {
        var logs = new RecordingLoggerFactory();
        using var factory = Create(new FakeTimeProvider(), loggerFactory: logs);

        var canonical = factory.Get("0.1.1.1.1.1", 851);
        Assert.Empty(logs.Warnings);                    // nothing laundered yet

        var laundered = factory.Get("999.1.1.1.1.1", 851);

        Assert.Same(canonical, laundered);              // one device, one channel
        var warning = Assert.Single(logs.Warnings);
        Assert.Contains("999.1.1.1.1.1", warning);
        Assert.Contains("0.1.1.1.1.1", warning);
    }

    /// <summary>
    /// Totality across every shape of present-but-unusable Net ID.
    /// </summary>
    /// <remarks>
    /// The empty and whitespace rows are the ones that matter: <c>AmsNetId.TryParse</c>
    /// is itself NOT total — it THROWS <see cref="ArgumentException"/> on an empty
    /// string instead of returning false, and trimming turns <c>"   "</c> into one —
    /// so normalising without an emptiness guard breaks the documented contract on
    /// exactly the input a discovery scan is most likely to hand over.
    /// </remarks>
    [Theory]
    [InlineData("99.99.99.99.1.1", 12345)]      // well-formed but unreachable
    [InlineData("not-a-net-id", 851)]           // unparseable
    [InlineData("1.2.3.4.5", 851)]              // too few octets
    [InlineData("", 851)]                       // empty
    [InlineData("   ", 851)]                    // whitespace only
    public void Get_IsTotal_ForAnyPresentNetId(string amsNetId, int port)
    {
        using var factory = Create(new FakeTimeProvider());

        var channel = factory.Get(amsNetId, port);

        Assert.NotNull(channel);
        Assert.Equal(ConnectionState.Disconnected, channel.State);
        Assert.Same(channel, factory.Get(amsNetId, port));   // and still cached
    }

    /// <summary>
    /// <c>TryGetSimulated</c> normalises through the same helper, so it inherits
    /// the same totality obligation.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-net-id")]
    public void TryGetSimulated_IsTotal_ForAnyPresentNetId(string amsNetId)
    {
        using var factory = Create(new FakeTimeProvider());

        Assert.True(factory.TryGetSimulated(amsNetId, 851, out var sim));
        Assert.NotNull(sim);
    }

    /// <summary>
    /// A null Net ID is a caller programming error, not a target that happens not
    /// to exist, so it is the one input totality does NOT cover.
    /// </summary>
    /// <remarks>
    /// Pinned because the alternative is an accident: before the guard this threw
    /// <see cref="NullReferenceException"/> from <c>Trim()</c>, and before
    /// normalisation existed it threw <see cref="ArgumentNullException"/> out of
    /// the key comparer. Neither was anyone's intended contract.
    /// </remarks>
    [Fact]
    public void NullNetId_ThrowsArgumentNullException()
    {
        using var factory = Create(new FakeTimeProvider());

        Assert.Throws<ArgumentNullException>(() => factory.Get(null!, 851));
        Assert.Throws<ArgumentNullException>(() => factory.TryGetSimulated(null!, 851, out _));
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
    /// After teardown, <c>Get</c> stays total but operating fails fast instead of
    /// opening a transport nothing would ever dispose.
    /// </summary>
    /// <remarks>
    /// The raw factory is registered last so it stops FIRST; a consumer hosted
    /// service stopping afterwards can still reach <c>Get</c>. Without the guard
    /// its next operation mints a live <c>AdsClient</c> after the shutdown sweep
    /// has already passed — the same ownership family as #9/#13/#15. Failing fast
    /// rather than waiting mirrors the pool's documented rule for a stopped pool.
    /// </remarks>
    [Theory]
    [InlineData(true)]      // via StopAsync
    [InlineData(false)]     // via Dispose
    public async Task AfterTeardown_GetIsStillTotal_ButOperatingFailsFast(bool viaStopAsync)
    {
        var clock = new FakeTimeProvider();
        using var factory = Create(clock);
        await factory.StartAsync(CancellationToken.None);

        if (viaStopAsync)
            await factory.StopAsync(CancellationToken.None);
        else
            factory.Dispose();

        var channel = factory.Get("1.2.3.4.5.6", 851);
        Assert.NotNull(channel);                                    // Get never throws
        Assert.Equal(ConnectionState.Disconnected, channel.State);

        factory.TryGetSimulated("1.2.3.4.5.6", 851, out var sim);
        sim!.Seed(0x11, 1, [1]);

        await Assert.ThrowsAsync<AdsConnectionUnavailableException>(
            () => channel.ReadAsync(0x11, 1, new byte[1], CancellationToken.None));

        // Still no transport: the delegate refused rather than constructing one.
        Assert.Equal(ConnectionState.Disconnected, channel.State);
    }

    /// <summary>
    /// A channel obtained and connected BEFORE teardown must also refuse to
    /// re-open afterwards — its transport was released, and reconnecting would
    /// leak exactly as a fresh one would.
    /// </summary>
    [Fact]
    public async Task AfterTeardown_APreviouslyConnectedChannel_DoesNotReconnect()
    {
        var clock = new FakeTimeProvider();
        using var factory = Create(clock);

        var (first, _) = await ConnectTwoAsync(factory);

        factory.Dispose();
        Assert.Equal(ConnectionState.Disconnected, first.State);

        await Assert.ThrowsAsync<AdsConnectionUnavailableException>(
            () => first.ReadAsync(0x11, 1, new byte[1], CancellationToken.None));

        Assert.Equal(ConnectionState.Disconnected, first.State);
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

    /// <summary>
    /// The client bound must sit above every bound the channel can construct, for
    /// both configurations that would otherwise cap one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 750 is the dangerous one. <c>ReadAsync(..., TimeSpan timeout, ct)</c> is
    /// documented to override <c>TimeoutMs</c> for one attempt, and the channel
    /// honours it — but <c>AdsClient.Timeout</c> is a per-CLIENT property shared by
    /// every concurrent caller, so wiring it to <c>TimeoutMs</c> would cut a 2 s
    /// override off at 750 ms and raise <c>AdsErrorException</c>/<c>ClientSyncTimeOut</c>,
    /// which this library's contract defines as a device ANSWER. A timeout wearing
    /// an answer's clothes.
    /// </para>
    /// <para>
    /// 30000 is the original defect: left unassigned the client keeps Beckhoff's own
    /// 5000 ms default, so a configured bound above it is simply unreachable — and
    /// fails in that same wrong shape.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(750)]
    [InlineData(30_000)]
    public void RealTransport_ClientBoundNeverPreemptsTheChannelsOwn(int configuredTimeoutMs)
    {
        using var factory = Create(
            new FakeTimeProvider(),
            o => { o.Mode = ConnectionMode.Real; o.TimeoutMs = configuredTimeoutMs; });

        // Constructs the client; it does not connect, so no router is involved.
        using var transport = factory.CreateTransport("1.2.3.4.5.6", 0xFFFF);

        var clientBoundMs = Assert.IsType<BeckhoffManagedRawConnection>(transport).ClientTimeoutMs;

        // Deliberately a floor, not an equality: the backstop's only job is to never
        // fire first, so pinning an exact value would just re-pin an implementation
        // detail. An hour is far beyond any per-call override a real caller passes.
        Assert.True(
            clientBoundMs >= (int)TimeSpan.FromHours(1).TotalMilliseconds,
            $"client bound {clientBoundMs} ms can preempt a per-call override on a " +
            $"channel configured at {configuredTimeoutMs} ms");
    }
}
