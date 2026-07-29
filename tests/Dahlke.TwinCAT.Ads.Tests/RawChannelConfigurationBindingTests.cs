using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Binds <c>RawChannels</c> from a REAL <see cref="IConfiguration"/> through the
/// same registration path a host uses.
/// </summary>
/// <remarks>
/// <para>
/// Every other raw-channel test builds <see cref="TwinCatAdsOptions"/> in code,
/// which is exactly how a completely dead configuration section shipped: the
/// section was never bound, so a host writing
/// <c>"RawChannels": { "Mode": "Simulated" }</c> got <see cref="ConnectionMode.Real"/>
/// and silently started the AMS router. Nothing in the suite could see it.
/// </para>
/// <para>
/// These tests therefore go through <c>AddTwinCatAds(IConfiguration)</c> and
/// resolve <see cref="IOptions{TOptions}"/>, and one of them goes all the way to
/// the factory — a configured seed has to survive binding, validation AND
/// materialisation, and each of those has silently dropped it at some point.
/// </para>
/// </remarks>
public class RawChannelConfigurationBindingTests
{
    /// <summary>
    /// A minimal valid PLC target. Startup validation rejects an empty
    /// <c>Targets</c> collection, so every case needs one; it is scaffolding, not
    /// part of what is under test.
    /// </summary>
    private const string TargetKey = "PlcTargets:plc1:AmsNetId";

    private static TwinCatAdsOptions Resolve(
        Dictionary<string, string?> settings,
        bool simulation = false)
    {
        settings[TargetKey] = "1.2.3.4.5.6";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        if (simulation)
            services.AddTwinCatAdsSimulation(configuration);
        else
            services.AddTwinCatAds(configuration);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;
    }

    // ------------------------------------------------------------------
    // Defect (a): the section was never bound
    // ------------------------------------------------------------------

    /// <summary>
    /// The single most consequential case: a host asks for simulation in
    /// configuration and nothing else. Before the fix this returned
    /// <see cref="ConnectionMode.Real"/> and started an embedded AMS router.
    /// </summary>
    [Fact]
    public void Mode_BindsFromConfigurationAlone()
    {
        var options = Resolve(new() { ["RawChannels:Mode"] = "Simulated" });

        Assert.Equal(ConnectionMode.Simulated, options.RawChannels.Mode);
    }

    [Fact]
    public void Scalars_BindFromConfiguration()
    {
        var options = Resolve(new()
        {
            ["RawChannels:TimeoutMs"]      = "1234",
            ["RawChannels:RetryCount"]     = "3",
            ["RawChannels:IdleEvictionMs"] = "9000",
        });

        Assert.Equal(1234, options.RawChannels.TimeoutMs);
        Assert.Equal(3, options.RawChannels.RetryCount);
        Assert.Equal(9000, options.RawChannels.IdleEvictionMs);
    }

    [Fact]
    public void Defaults_SurviveAnAbsentSection()
    {
        var options = Resolve(new());

        Assert.Equal(ConnectionMode.Real, options.RawChannels.Mode);
        Assert.Equal(5000, options.RawChannels.TimeoutMs);
        Assert.Equal(1, options.RawChannels.RetryCount);
        Assert.Equal(60_000, options.RawChannels.IdleEvictionMs);
        Assert.Empty(options.RawChannels.Seed);
    }

    // ------------------------------------------------------------------
    // Defect (b): the seed shape could not survive binding
    // ------------------------------------------------------------------

    /// <summary>
    /// The seed arrives whole — Net ID, port, slot indices and payload.
    /// </summary>
    /// <remarks>
    /// The old shape keyed the outer dictionary on <c>"netId:port"</c> and the
    /// inner one on <c>"indexGroup:indexOffset"</c>. <c>:</c> is the configuration
    /// HIERARCHY SEPARATOR, so those keys flattened into nested sections and bound
    /// to an outer entry with zero slots — silently. Hence the array-of-objects
    /// shape, which has no separator to collide with.
    /// </remarks>
    [Fact]
    public void Seed_BindsWholeFromConfiguration()
    {
        var options = Resolve(new()
        {
            ["RawChannels:Mode"]                        = "Simulated",
            ["RawChannels:Seed:0:AmsNetId"]             = "192.168.1.10.3.1",
            ["RawChannels:Seed:0:Port"]                 = "65535",
            ["RawChannels:Seed:0:Slots:0:IndexGroup"]   = "0x11",
            ["RawChannels:Seed:0:Slots:0:IndexOffset"]  = "1001",
            ["RawChannels:Seed:0:Slots:0:Bytes"]        = "02000000410C0000",
        });

        var seed = Assert.Single(options.RawChannels.Seed);
        Assert.Equal("192.168.1.10.3.1", seed.AmsNetId);
        Assert.Equal(65535, seed.Port);

        var slot = Assert.Single(seed.Slots);
        Assert.Equal("0x11", slot.IndexGroup);
        Assert.Equal("1001", slot.IndexOffset);
        Assert.Equal("02000000410C0000", slot.Bytes);
    }

    /// <summary>
    /// The same seed written as the JSON a host actually pastes into
    /// <c>appsettings.json</c> — the documented shape, parsed by the real JSON
    /// provider rather than assembled key by key.
    /// </summary>
    [Fact]
    public void Seed_BindsFromTheDocumentedJsonShape()
    {
        const string json = """
            {
              "PlcTargets": { "plc1": { "AmsNetId": "1.2.3.4.5.6" } },
              "RawChannels": {
                "Mode": "Simulated",
                "TimeoutMs": 4000,
                "Seed": [
                  {
                    "AmsNetId": "192.168.1.10.3.1",
                    "Port": 65535,
                    "Slots": [
                      { "IndexGroup": "0x11", "IndexOffset": 1001, "Bytes": "02000000410C0000" }
                    ]
                  }
                ]
              }
            }
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        Assert.Equal(ConnectionMode.Simulated, options.RawChannels.Mode);
        Assert.Equal(4000, options.RawChannels.TimeoutMs);

        var seed = Assert.Single(options.RawChannels.Seed);
        Assert.Equal("192.168.1.10.3.1", seed.AmsNetId);
        Assert.Equal(65535, seed.Port);

        var slot = Assert.Single(seed.Slots);
        Assert.Equal("0x11", slot.IndexGroup);
        Assert.Equal("1001", slot.IndexOffset);
        Assert.Equal("02000000410C0000", slot.Bytes);
    }

    /// <summary>
    /// Binding is only half the promise: the bytes have to reach a channel.
    /// </summary>
    /// <remarks>
    /// This is the end-to-end path a host exercises — configuration to
    /// <c>IAdsRawChannelFactory.Get(...).ReadAsync(...)</c> — so a break anywhere
    /// between the binder, the validator and <c>CreateStore</c> shows up here.
    /// </remarks>
    [Fact]
    public async Task ConfiguredSeed_ReachesTheChannel()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [TargetKey]                                = "1.2.3.4.5.6",
                ["RawChannels:Mode"]                       = "Simulated",
                ["RawChannels:Seed:0:AmsNetId"]            = "192.168.1.10.3.1",
                ["RawChannels:Seed:0:Port"]                = "65535",
                ["RawChannels:Seed:0:Slots:0:IndexGroup"]  = "0x11",
                ["RawChannels:Seed:0:Slots:0:IndexOffset"] = "1001",
                ["RawChannels:Seed:0:Slots:0:Bytes"]       = "02000000",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(configuration);

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IAdsRawChannelFactory>();

        var buffer = new byte[4];
        var read = await factory
            .Get("192.168.1.10.3.1", 65535)
            .ReadAsync(0x11, 1001, buffer, CancellationToken.None);

        Assert.Equal(4, read);
        Assert.Equal([0x02, 0x00, 0x00, 0x00], buffer);
    }

    /// <summary>
    /// A seed entry spelled non-canonically must still reach the channel it names.
    /// </summary>
    /// <remarks>
    /// The factory normalises a caller's Net ID (trim, then canonicalise), so the
    /// CONFIGURED id has to be normalised the same way or the two never meet. This
    /// bug has been fixed once already on the runtime path; the restructure is
    /// exactly the kind of change that would reintroduce it.
    /// </remarks>
    [Fact]
    public async Task ConfiguredSeed_MatchesOnTheNormalisedNetId()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [TargetKey]                                = "1.2.3.4.5.6",
                ["RawChannels:Mode"]                       = "Simulated",
                ["RawChannels:Seed:0:AmsNetId"]            = "01.2.3.4.5.6",  // leading zero
                ["RawChannels:Seed:0:Port"]                = "851",
                ["RawChannels:Seed:0:Slots:0:IndexGroup"]  = "0x11",
                ["RawChannels:Seed:0:Slots:0:IndexOffset"] = "1001",
                ["RawChannels:Seed:0:Slots:0:Bytes"]       = "7B",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(configuration);

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IAdsRawChannelFactory>();

        var buffer = new byte[1];
        var read = await factory
            .Get("1.2.3.4.5.6", 851)
            .ReadAsync(0x11, 1001, buffer, CancellationToken.None);

        Assert.Equal(1, read);
        Assert.Equal(0x7B, buffer[0]);
    }

    // ------------------------------------------------------------------
    // Ordering: PostConfigure runs after binding
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>AddTwinCatAdsSimulation</c> must beat configuration, not the other way
    /// round: its whole promise is that no hardware is needed, and binding now runs
    /// BEFORE the PostConfigure that enforces it.
    /// </summary>
    [Fact]
    public void SimulationPostConfigure_OverridesAConfiguredRealMode()
    {
        var options = Resolve(
            new() { ["RawChannels:Mode"] = "Real" },
            simulation: true);

        Assert.Equal(ConnectionMode.Simulated, options.RawChannels.Mode);
    }

    /// <summary>
    /// The mirror image: the non-simulation helper leaves a configured
    /// <see cref="ConnectionMode.Real"/> alone, so the assertion above is really
    /// about PostConfigure and not about the binder ignoring the value.
    /// </summary>
    [Fact]
    public void RealHelper_LeavesAConfiguredRealModeAlone()
    {
        var options = Resolve(new() { ["RawChannels:Mode"] = "Real" });

        Assert.Equal(ConnectionMode.Real, options.RawChannels.Mode);
    }

    /// <summary>
    /// The combo overload's documented ordering, applied to raw channels: binding
    /// first, then the lambda. A lambda that appends to <c>Seed</c> must not be
    /// erased by the bind, and one that sets a scalar must win.
    /// </summary>
    [Fact]
    public void ComboOverload_LambdaLayersOnTopOfBinding()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [TargetKey]                     = "1.2.3.4.5.6",
                ["RawChannels:Mode"]            = "Simulated",
                ["RawChannels:TimeoutMs"]       = "1234",
                ["RawChannels:Seed:0:AmsNetId"] = "1.2.3.4.5.6",
                ["RawChannels:Seed:0:Port"]     = "851",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(configuration, o =>
        {
            o.RawChannels.TimeoutMs = 2222;
            o.RawChannels.Seed.Add(new AdsRawChannelSeed
            {
                AmsNetId = "9.9.9.9.9.9",
                Port = 852,
            });
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        Assert.Equal(2222, options.RawChannels.TimeoutMs);
        Assert.Equal(2, options.RawChannels.Seed.Count);
        Assert.Contains(options.RawChannels.Seed, s => s.AmsNetId == "1.2.3.4.5.6");
        Assert.Contains(options.RawChannels.Seed, s => s.AmsNetId == "9.9.9.9.9.9");
    }

    // ------------------------------------------------------------------
    // Validation still runs on the bound values
    // ------------------------------------------------------------------

    /// <summary>
    /// A typo in configuration must fail the host at startup rather than sit
    /// silently broken — the same standard the code-first path already meets.
    /// </summary>
    [Fact]
    public void MalformedConfiguredSeed_FailsAtStartup()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new()
        {
            ["RawChannels:Seed:0:AmsNetId"]            = "999.1.1.1.1.1",
            ["RawChannels:Seed:0:Port"]                = "851",
            ["RawChannels:Seed:0:Slots:0:IndexGroup"]  = "0x11",
            ["RawChannels:Seed:0:Slots:0:IndexOffset"] = "1001",
            ["RawChannels:Seed:0:Slots:0:Bytes"]       = "00",
        }));

        Assert.Contains(ex.Failures, f => f.Contains("999.1.1.1.1.1"));
    }

    /// <summary>
    /// A degenerate array element must produce a NAMED startup failure, not a
    /// <see cref="NullReferenceException"/> from inside the validator.
    /// </summary>
    /// <remarks>
    /// Probed rather than assumed: the binder turns both a JSON <c>null</c> and an
    /// empty object into an <see cref="AdsRawChannelSeed"/> with default members —
    /// it never puts a <see langword="null"/> into the list — so the empty
    /// <see cref="AdsRawChannelSeed.AmsNetId"/> is what fails, and it fails saying
    /// which entry. That is the whole reason the validator needs no null guard.
    /// </remarks>
    [Fact]
    public void DegenerateSeedElement_FailsAtStartupByName_RatherThanCrashing()
    {
        const string json = """
            {
              "PlcTargets": { "plc1": { "AmsNetId": "1.2.3.4.5.6" } },
              "RawChannels": { "Seed": [ null, {} ] }
            }
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TwinCatAdsOptions>>();

        var ex = Assert.Throws<OptionsValidationException>(() => _ = options.Value);

        Assert.Contains(ex.Failures, f => f.Contains("RawChannels:Seed:0:AmsNetId"));
        Assert.Contains(ex.Failures, f => f.Contains("RawChannels:Seed:1:AmsNetId"));
    }

    // ------------------------------------------------------------------
    // Port: hex works, and a value the binder cannot convert is not silent
    // ------------------------------------------------------------------

    /// <summary>
    /// A configured <c>Port</c> accepts <c>0x</c>-prefixed hex as well as decimal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pinned because it is easy to assume otherwise — <c>Port</c> is an
    /// <see cref="int"/>, and this task's own report initially claimed the hex form
    /// had been lost in the move from the composite key. It has not:
    /// <c>ConfigurationBinder</c> converts through
    /// <see cref="System.ComponentModel.Int32Converter"/>, which honours a <c>0x</c>
    /// prefix. That matters here specifically, because <c>0xFFFF</c> is how this
    /// domain writes the EtherCAT master port and how the README writes it two
    /// sections above the JSON example.
    /// </para>
    /// <para>
    /// It is behaviour inherited from the binder rather than chosen here, which is
    /// exactly why it needs a test: nothing in this repository would otherwise notice
    /// it changing.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("65535", 65535)]
    [InlineData("0xFFFF", 65535)]
    [InlineData("0XFFFF", 65535)]
    [InlineData("851", 851)]
    public void SeedPort_AcceptsDecimalAndHex(string configured, int expected)
    {
        var options = Resolve(new()
        {
            ["RawChannels:Mode"]            = "Simulated",
            ["RawChannels:Seed:0:AmsNetId"] = "1.2.3.4.5.6",
            ["RawChannels:Seed:0:Port"]     = configured,
        });

        Assert.Equal(expected, Assert.Single(options.RawChannels.Seed).Port);
    }

    /// <summary>
    /// A <c>Port</c> the binder cannot convert must fail the host by name, not vanish.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The binder discards it silently.</b> A failed conversion in a SCALAR throws
    /// <see cref="InvalidOperationException"/> naming the path; the same failure
    /// inside a COLLECTION ELEMENT is swallowed and the element is dropped, so this
    /// configuration binds to an empty <c>Seed</c> with no error at all. That is the
    /// silent-seed-loss this task exists to eliminate, arriving by a different route
    /// than the one the brief described.
    /// </para>
    /// <para>
    /// Note the good entry survives, which is what makes the bare version of this so
    /// hard to notice: seeding appears to work, just not for one target.
    /// </para>
    /// </remarks>
    [Fact]
    public void SeedEntryDiscardedByTheBinder_FailsAtStartup()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new()
        {
            ["RawChannels:Mode"]            = "Simulated",
            ["RawChannels:Seed:0:AmsNetId"] = "1.2.3.4.5.6",
            ["RawChannels:Seed:0:Port"]     = "not-a-port",
            ["RawChannels:Seed:1:AmsNetId"] = "9.9.9.9.9.9",
            ["RawChannels:Seed:1:Port"]     = "852",
        }));

        Assert.Contains(
            ex.Failures,
            f => f.Contains("RawChannels:Seed") && f.Contains("DISCARDED"));
    }

    /// <summary>
    /// The same protection through the JSON provider, for the two forms a host is
    /// most likely to produce by accident: a <c>null</c> and an empty string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written against JSON specifically, because the providers do NOT agree and only
    /// this one matters to a host with an <c>appsettings.json</c>. The JSON provider
    /// represents a JSON <c>null</c> as an EMPTY STRING, which fails to convert to
    /// <see cref="int"/> and so drops the element — caught here. An in-memory
    /// collection holding a CLR <see langword="null"/> for the same key behaves
    /// differently: the binder skips the property and <c>Port</c> keeps its default of
    /// <c>0</c>, with no drop and no failure. Probed, because an in-memory test would
    /// have quietly proved the wrong thing.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"typo\"")]
    public void SeedEntryWithAnUnconvertiblePortInJson_FailsAtStartup(string portLiteral)
    {
        var json = $$"""
            {
              "PlcTargets": { "plc1": { "AmsNetId": "1.2.3.4.5.6" } },
              "RawChannels": {
                "Mode": "Simulated",
                "Seed": [ { "AmsNetId": "1.2.3.4.5.6", "Port": {{portLiteral}} } ]
              }
            }
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TwinCatAdsOptions>>();

        var ex = Assert.Throws<OptionsValidationException>(() => _ = options.Value);

        Assert.Contains(
            ex.Failures,
            f => f.Contains("RawChannels:Seed") && f.Contains("DISCARDED"));
    }

    /// <summary>
    /// The shortfall check must not fire when nothing was discarded — including for
    /// the one case that legitimately produces MORE bound entries than configured
    /// ones.
    /// </summary>
    /// <remarks>
    /// Registering twice adds the binding delegate twice, and <c>Bind</c> APPENDS to a
    /// list, so the bound count doubles. Hence the check is a shortfall
    /// (<c>bound &lt; configured</c>) and not an inequality — an equality test here
    /// would fail every host that calls <c>AddTwinCatAds</c> twice.
    /// </remarks>
    [Fact]
    public void DoubleRegistration_DoesNotReportADiscardedEntry()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [TargetKey]                     = "1.2.3.4.5.6",
                ["RawChannels:Mode"]            = "Simulated",
                ["RawChannels:Seed:0:AmsNetId"] = "1.2.3.4.5.6",
                ["RawChannels:Seed:0:Port"]     = "851",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(configuration);
        services.AddTwinCatAds(configuration);

        using var provider = services.BuildServiceProvider();

        // Resolving is the assertion: a false shortfall would throw here.
        var options = provider.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        Assert.NotEmpty(options.RawChannels.Seed);
        Assert.All(options.RawChannels.Seed, s => Assert.Equal("1.2.3.4.5.6", s.AmsNetId));
    }

    /// <summary>
    /// Fails when a convertible-typed member is added to a seed type, because the
    /// discard check covers the OUTER seed list only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A slot is itself a collection element, so the binder drops an unconvertible slot
    /// exactly as it drops an unconvertible entry — leaving the entry one slot short
    /// while the outer counts still agree. Verified by temporarily adding an
    /// <see cref="int"/> slot member: outer configured 1 / bound 1, slots configured 2 /
    /// bound 1, and <c>RecordDiscardedSeedEntries</c> silent.
    /// </para>
    /// <para>
    /// <b>It is unreachable today, which is why this guard exists instead of a slot-level
    /// check.</b> Every <see cref="AdsRawChannelSeedSlot"/> member is a
    /// <see cref="string"/>, and a string never fails to convert, so no configuration
    /// can trigger a slot discard — a slot-level check would have been code no test
    /// could exercise. This test is the alternative: it converts a latent trap into a
    /// loud one at the moment the assumption stops holding.
    /// </para>
    /// <para>
    /// <see cref="AdsRawChannelSeed.Port"/> is the one permitted exception; it is
    /// covered, being a member of an OUTER entry.
    /// </para>
    /// </remarks>
    [Fact]
    public void SeedTypesStayStringOnly_SoTheDiscardCheckRemainsComplete()
    {
        var offenders = new List<string>();

        foreach (var type in new[] { typeof(AdsRawChannelSeed), typeof(AdsRawChannelSeedSlot) })
        {
            foreach (var property in type.GetProperties(
                         System.Reflection.BindingFlags.Public |
                         System.Reflection.BindingFlags.Instance))
            {
                if (property.SetMethod is not { IsPublic: true })
                    continue;

                // Strings never fail to convert; a List<T> of them is walked as its own
                // collection. Anything else is convertible-typed and needs thought.
                if (property.PropertyType == typeof(string) ||
                    property.PropertyType.IsGenericType)
                    continue;

                // The known, covered exception.
                if (type == typeof(AdsRawChannelSeed) && property.Name == nameof(AdsRawChannelSeed.Port))
                    continue;

                offenders.Add($"{type.Name}.{property.Name} ({property.PropertyType.Name})");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"A convertible-typed member was added to a seed type: {string.Join(", ", offenders)}. " +
            "ConfigurationBinder DISCARDS a collection element whose value will not convert, " +
            "silently. RecordDiscardedSeedEntries in ServiceCollectionExtensions compares the " +
            "OUTER seed list only, so a bad value on a SLOT member is not caught — the entry " +
            "binds one slot short with no error. Extend that check to slot counts, add a test " +
            "for it (now possible, since a convertible-typed member exists to break), and add " +
            "the member here if it is on an outer entry and therefore already covered.");
    }
}
