using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Tests for the code-first <c>AddTwinCatAds(Action&lt;TwinCatAdsOptions&gt;)</c> and
/// <c>AddTwinCatAds(IConfiguration, Action&lt;TwinCatAdsOptions&gt;)</c> overloads
/// (and their simulation counterparts).  No IConfiguration is registered in the
/// pure code-first scenarios.
/// </summary>
public class CodeFirstRegistrationTests
{
    // ------------------------------------------------------------------
    // Pure code-first: AddTwinCatAds(Action<TwinCatAdsOptions>)
    // ------------------------------------------------------------------

    [Fact]
    public void CodeFirst_AddTwinCatAds_ProviderBuilds_WithoutIConfiguration()
    {
        // Arrange — no IConfiguration anywhere in the service collection.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
        });

        // Act — should not throw; no IConfiguration required.
        using var sp = services.BuildServiceProvider();

        // Assert — the options are resolved and contain the code-first value.
        var opts = sp.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;
        Assert.Single(opts.Targets);
        Assert.Equal("1.2.3.4.5.6", opts.Targets["plc1"].AmsNetId);
    }

    [Fact]
    public void CodeFirst_AddTwinCatAds_IAdsConnectionPool_ResolvesWithoutIConfiguration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
        });

        using var sp = services.BuildServiceProvider();

        // IAdsConnectionPool must resolve — this proves service wiring is complete.
        var pool = sp.GetRequiredService<IAdsConnectionPool>();
        Assert.NotNull(pool);
    }

    [Fact]
    public void CodeFirst_AddTwinCatAds_InvalidLambda_ThrowsOptionsValidationException()
    {
        // Arrange — lambda leaves Targets empty → should fail validation.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(_ => { /* no targets added */ });

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<TwinCatAdsOptions>>();

        // Act & Assert — validation is wired up via ValidateOnStart.
        Assert.Throws<OptionsValidationException>(() => _ = opts.Value);
    }

    [Fact]
    public void CodeFirst_AddTwinCatAds_MultipleTargets_AllVisible()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
            o.Targets["plc2"] = new PlcTargetOptions { AmsNetId = "6.5.4.3.2.1" };
        });

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        Assert.Equal(2, opts.Targets.Count);
        Assert.Equal("6.5.4.3.2.1", opts.Targets["plc2"].AmsNetId);
    }

    // ------------------------------------------------------------------
    // Combo overload: AddTwinCatAds(IConfiguration, Action<TwinCatAdsOptions>)
    // ------------------------------------------------------------------

    [Fact]
    public void Combo_AddTwinCatAds_LambdaRunsAfterConfigBinding()
    {
        // Config defines plc1 and Prefixes:0=GVL.
        // Lambda adds a second prefix and a second target.
        // Both config and lambda contributions must survive.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlcTargets:plc1:AmsNetId"]    = "1.2.3.4.5.6",
                ["AdsSymbolDump:Prefixes:0"]    = "GVL",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(config, o =>
        {
            // Mutate list — proves lambda runs AFTER binding so the list isn't
            // cleared by a subsequent Bind call.
            o.Diagnostics.SymbolDump.Prefixes.Add("MAIN");
            // Add second target — proves lambda can add things not in config.
            o.Targets["plc2"] = new PlcTargetOptions { AmsNetId = "6.5.4.3.2.1" };
        });

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        // Targets: plc1 from config, plc2 from lambda.
        Assert.Equal(2, opts.Targets.Count);
        Assert.Equal("1.2.3.4.5.6", opts.Targets["plc1"].AmsNetId);
        Assert.Equal("6.5.4.3.2.1", opts.Targets["plc2"].AmsNetId);

        // Prefixes: "GVL" from config, "MAIN" appended by lambda — both present.
        Assert.Contains("GVL", opts.Diagnostics.SymbolDump.Prefixes);
        Assert.Contains("MAIN", opts.Diagnostics.SymbolDump.Prefixes);
    }

    [Fact]
    public void Combo_AddTwinCatAds_LambdaCanOverrideConfigValues()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlcTargets:plc1:AmsNetId"] = "1.2.3.4.5.6",
                ["PlcTargets:plc1:Port"]     = "801",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(config, o =>
        {
            // Override the port set by config.
            if (o.Targets.TryGetValue("plc1", out var t))
                t.Port = 851;
        });

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        Assert.Equal(851, opts.Targets["plc1"].Port);
    }

    // ------------------------------------------------------------------
    // Simulation code-first overload
    // ------------------------------------------------------------------

    [Fact]
    public void CodeFirst_AddTwinCatAdsSimulation_ProviderBuilds_WithoutIConfiguration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions { AmsNetId = "127.0.0.1.1.1" };
        });

        using var sp = services.BuildServiceProvider();

        var pool = sp.GetRequiredService<IAdsConnectionPool>();
        Assert.NotNull(pool);

        var opts = sp.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;
        Assert.Single(opts.Targets);
        Assert.Equal("127.0.0.1.1.1", opts.Targets["sim1"].AmsNetId);
    }

    [Fact]
    public void CodeFirst_AddTwinCatAdsSimulation_InvalidLambda_ThrowsOptionsValidationException()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(_ => { /* no targets */ });

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<TwinCatAdsOptions>>();

        Assert.Throws<OptionsValidationException>(() => _ = opts.Value);
    }

    // ------------------------------------------------------------------
    // Simulation combo overload
    // ------------------------------------------------------------------

    [Fact]
    public void Combo_AddTwinCatAdsSimulation_ConfigPlusLambda_BothContribute()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlcTargets:sim1:AmsNetId"] = "127.0.0.1.1.1",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(config, o =>
        {
            o.Targets["sim2"] = new PlcTargetOptions { AmsNetId = "127.0.0.2.1.1" };
        });

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        Assert.Equal(2, opts.Targets.Count);
        Assert.True(opts.Targets.ContainsKey("sim1"));
        Assert.True(opts.Targets.ContainsKey("sim2"));
    }

    // ------------------------------------------------------------------
    // IHostedService resolution — pure code-first, no IConfiguration
    // ------------------------------------------------------------------

    /// <summary>
    /// Confirms that resolving all IHostedService registrations succeeds when no
    /// IConfiguration is registered (Router.NetId unset / null).
    /// Before the fix, MS.DI tried to inject IConfiguration into AdsRouterService
    /// and threw InvalidOperationException even though the parameter is nullable.
    /// </summary>
    [Fact]
    public void CodeFirst_AddTwinCatAds_GetHostedServices_Succeeds_RouterNetIdUnset()
    {
        // Arrange — pure code-first: no IConfiguration anywhere.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
            // Router.NetId intentionally left null/empty
        });

        using var sp = services.BuildServiceProvider();

        // Act — GetServices materialises all IHostedService instances.
        // Constructing hosted services does NOT start them, so this is safe in a
        // unit test context.
        var hostedServices = sp.GetServices<IHostedService>().ToList();

        // Assert — both AdsRouterService and AdsConnectionPool must be present.
        Assert.Contains(hostedServices, s => s is AdsRouterService);
        Assert.Contains(hostedServices, s => s is AdsConnectionPool);
    }

    /// <summary>
    /// Same as above but with Router.NetId explicitly set to a loopback address.
    /// Verifies the factory path that would choose AmsTcpIpRouter(AmsNetId, …)
    /// still constructs the service without IConfiguration.
    /// </summary>
    [Fact]
    public void CodeFirst_AddTwinCatAds_GetHostedServices_Succeeds_RouterNetIdSet()
    {
        // Arrange — pure code-first with a Net ID specified.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
            o.Router.NetId = "127.0.0.1.1.1";
        });

        using var sp = services.BuildServiceProvider();

        // Act
        var hostedServices = sp.GetServices<IHostedService>().ToList();

        // Assert
        Assert.Contains(hostedServices, s => s is AdsRouterService);
        Assert.Contains(hostedServices, s => s is AdsConnectionPool);
    }

    // ------------------------------------------------------------------
    // Existing config-only overloads must still work identically
    // ------------------------------------------------------------------

    [Fact]
    public void ConfigOnly_AddTwinCatAds_BackwardCompatible()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlcTargets:plc1:AmsNetId"] = "1.2.3.4.5.6",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(config);

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        Assert.Single(opts.Targets);
        Assert.Equal("1.2.3.4.5.6", opts.Targets["plc1"].AmsNetId);
    }
}
