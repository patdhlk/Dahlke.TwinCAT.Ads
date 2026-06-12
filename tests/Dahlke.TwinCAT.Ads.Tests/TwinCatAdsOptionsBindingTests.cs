using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Tests that <see cref="TwinCatAdsOptions"/> is correctly populated from the
/// existing configuration layout when <c>AddTwinCatAds</c> (or
/// <c>AddTwinCatAdsSimulation</c>) is called. No hosted services are started —
/// only <see cref="IOptions{TOptions}"/> is resolved.
/// </summary>
public class TwinCatAdsOptionsBindingTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ServiceProvider BuildProvider(
        Dictionary<string, string?> settings,
        bool simulation = false)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();

        if (simulation)
            services.AddTwinCatAdsSimulation(config);
        else
            services.AddTwinCatAds(config);

        return services.BuildServiceProvider();
    }

    private static TwinCatAdsOptions Resolve(
        Dictionary<string, string?> settings,
        bool simulation = false)
    {
        using var sp = BuildProvider(settings, simulation);
        return sp.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;
    }

    // ------------------------------------------------------------------
    // Targets
    // ------------------------------------------------------------------

    [Fact]
    public void Targets_BindFromPlcTargets()
    {
        var opts = Resolve(new()
        {
            ["PlcTargets:plc1:AmsNetId"]    = "1.2.3.4.5.6",
            ["PlcTargets:plc1:Port"]        = "801",
            ["PlcTargets:plc1:DisplayName"] = "Main PLC",
            ["PlcTargets:plc1:TimeoutMs"]   = "3000",
        });

        Assert.Single(opts.Targets);
        var t = opts.Targets["plc1"];
        Assert.Equal("1.2.3.4.5.6", t.AmsNetId);
        Assert.Equal(801, t.Port);
        Assert.Equal("Main PLC", t.DisplayName);
        Assert.Equal(3000, t.TimeoutMs);
    }

    [Fact]
    public void Targets_DefaultPortAndTimeout_WhenOmitted()
    {
        var opts = Resolve(new()
        {
            ["PlcTargets:plc2:AmsNetId"] = "5.6.7.8.9.0",
        });

        var t = opts.Targets["plc2"];
        Assert.Equal(851, t.Port);
        Assert.Equal(5000, t.TimeoutMs);
    }

    [Fact]
    public void Targets_LookupIsCaseInsensitive()
    {
        var opts = Resolve(new()
        {
            ["PlcTargets:MyPlc:AmsNetId"] = "1.2.3.4.5.6",
        });

        // The key was registered as "MyPlc" but the dictionary uses OrdinalIgnoreCase.
        Assert.True(opts.Targets.ContainsKey("myplc"));
        Assert.True(opts.Targets.ContainsKey("MYPLC"));
        Assert.True(opts.Targets.ContainsKey("MyPlc"));
    }

    [Fact]
    public void Targets_Empty_WhenNoPlcTargetsSection()
    {
        var opts = Resolve(new());
        Assert.Empty(opts.Targets);
    }

    // ------------------------------------------------------------------
    // Router
    // ------------------------------------------------------------------

    [Fact]
    public void Router_NetId_BindsFromAmsRouterSection()
    {
        var opts = Resolve(new()
        {
            ["AmsRouter:NetId"] = "192.168.0.1.1.1",
        });

        Assert.Equal("192.168.0.1.1.1", opts.Router.NetId);
    }

    [Fact]
    public void Router_NetId_IsNull_WhenAbsent()
    {
        var opts = Resolve(new());
        Assert.Null(opts.Router.NetId);
    }

    // ------------------------------------------------------------------
    // SymbolDump — defaults
    // ------------------------------------------------------------------

    [Fact]
    public void SymbolDump_Defaults_WhenNoConfiguration()
    {
        var opts = Resolve(new());

        Assert.False(opts.Diagnostics.SymbolDump.Enabled);
        Assert.Equal(1, opts.Diagnostics.SymbolDump.MaxDepth);
        Assert.Empty(opts.Diagnostics.SymbolDump.Prefixes);
    }

    // ------------------------------------------------------------------
    // SymbolDump — new AdsSymbolDump section
    // ------------------------------------------------------------------

    [Fact]
    public void SymbolDump_BindsFromAdsSymbolDumpSection()
    {
        var opts = Resolve(new()
        {
            ["AdsSymbolDump:Enabled"]      = "true",
            ["AdsSymbolDump:MaxDepth"]     = "5",
            ["AdsSymbolDump:Prefixes:0"]   = "MAIN",
            ["AdsSymbolDump:Prefixes:1"]   = "GVL",
        });

        Assert.True(opts.Diagnostics.SymbolDump.Enabled);
        Assert.Equal(5, opts.Diagnostics.SymbolDump.MaxDepth);
        Assert.Equal(["MAIN", "GVL"], opts.Diagnostics.SymbolDump.Prefixes);
    }

    // ------------------------------------------------------------------
    // SymbolDump — legacy key
    // ------------------------------------------------------------------

    [Fact]
    public void SymbolDump_LegacyKeyTrue_SetsEnabled()
    {
        var opts = Resolve(new()
        {
            ["AdsSymbolTreeDump"] = "true",
        });

        Assert.True(opts.Diagnostics.SymbolDump.Enabled);
        // Other defaults untouched.
        Assert.Equal(1, opts.Diagnostics.SymbolDump.MaxDepth);
        Assert.Empty(opts.Diagnostics.SymbolDump.Prefixes);
    }

    [Fact]
    public void SymbolDump_LegacyKeyFalse_DoesNotSetEnabled()
    {
        var opts = Resolve(new()
        {
            ["AdsSymbolTreeDump"] = "false",
        });

        Assert.False(opts.Diagnostics.SymbolDump.Enabled);
    }

    [Fact]
    public void SymbolDump_NewSectionOverridesLegacyKey()
    {
        // Legacy says enabled=true, new section says enabled=false.
        // New section wins.
        var opts = Resolve(new()
        {
            ["AdsSymbolTreeDump"]      = "true",
            ["AdsSymbolDump:Enabled"]  = "false",
            ["AdsSymbolDump:MaxDepth"] = "3",
        });

        Assert.False(opts.Diagnostics.SymbolDump.Enabled);
        Assert.Equal(3, opts.Diagnostics.SymbolDump.MaxDepth);
    }

    // ------------------------------------------------------------------
    // Simulation variant also registers IOptions<TwinCatAdsOptions>
    // ------------------------------------------------------------------

    [Fact]
    public void SimulationVariant_AlsoBindsOptions()
    {
        var opts = Resolve(
            new()
            {
                ["PlcTargets:sim1:AmsNetId"] = "127.0.0.1.1.1",
                ["AmsRouter:NetId"]          = "127.0.0.2.1.1",
            },
            simulation: true);

        Assert.Single(opts.Targets);
        Assert.Equal("127.0.0.1.1.1", opts.Targets["sim1"].AmsNetId);
        Assert.Equal("127.0.0.2.1.1", opts.Router.NetId);
    }
}
