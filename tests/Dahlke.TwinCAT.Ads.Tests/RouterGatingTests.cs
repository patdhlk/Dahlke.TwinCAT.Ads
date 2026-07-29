using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Tests;

public class RouterGatingTests
{
    [Fact]
    public void SimulationHelper_ForcesRawChannelsToSimulated()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6", Port = 851 };
            o.RawChannels.Mode = ConnectionMode.Real;   // must be overridden
        });

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        Assert.Equal(ConnectionMode.Simulated, options.RawChannels.Mode);
        Assert.All(options.Targets.Values, t => Assert.Equal(ConnectionMode.Simulated, t.Mode));
    }

    [Fact]
    public void RouterIsNeeded_WhenOnlyRawChannelsAreReal()
    {
        var options = new TwinCatAdsOptions();
        options.Targets["sim"] = new PlcTargetOptions { Mode = ConnectionMode.Simulated };
        options.RawChannels.Mode = ConnectionMode.Real;

        Assert.True(AdsRouterService.NeedsRouter(options));
    }

    [Fact]
    public void RouterIsNotNeeded_WhenEverythingIsSimulated()
    {
        var options = new TwinCatAdsOptions();
        options.Targets["sim"] = new PlcTargetOptions { Mode = ConnectionMode.Simulated };
        options.RawChannels.Mode = ConnectionMode.Simulated;

        Assert.False(AdsRouterService.NeedsRouter(options));
    }

    [Fact]
    public void RouterIsNeeded_WhenATargetIsReal()
    {
        var options = new TwinCatAdsOptions();
        options.Targets["real"] = new PlcTargetOptions { Mode = ConnectionMode.Real };
        options.RawChannels.Mode = ConnectionMode.Simulated;

        Assert.True(AdsRouterService.NeedsRouter(options));
    }
}
