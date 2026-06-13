using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Tests that calling <c>AddTwinCatAds</c> or <c>AddTwinCatAdsSimulation</c>
/// more than once on the same <see cref="IServiceCollection"/> does NOT duplicate
/// singleton services or hosted services.
///
/// Duplicate registrations cause two routers fighting for one TCP port and two
/// connection pools competing over the same PLC targets.
/// </summary>
public class IdempotentRegistrationTests
{
    // ------------------------------------------------------------------
    // AddTwinCatAds idempotency
    // ------------------------------------------------------------------

    [Fact]
    public void AddTwinCatAds_CalledTwice_DoesNotDuplicateHostedServices()
    {
        // Calling AddTwinCatAds twice (e.g. library + consumer composition) must
        // not result in two AdsRouterService instances and two AdsConnectionPool
        // instances fighting over the same TCP port.
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddTwinCatAds(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
        });

        // Second call — simulates a downstream library also calling AddTwinCatAds.
        services.AddTwinCatAds(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
        });

        // Exactly 2 IHostedService registrations: one router + one pool (not 4).
        int hostedServiceCount = services
            .Count(d => d.ServiceType == typeof(IHostedService));

        Assert.Equal(2, hostedServiceCount);
    }

    [Fact]
    public void AddTwinCatAds_CalledTwice_DoesNotDuplicateAdsRouterReadySignal()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddTwinCatAds(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
        });
        services.AddTwinCatAds(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
        });

        int readySignalCount = services
            .Count(d => d.ServiceType == typeof(AdsRouterReadySignal));

        Assert.Equal(1, readySignalCount);
    }

    // ------------------------------------------------------------------
    // AddTwinCatAdsSimulation idempotency
    // ------------------------------------------------------------------

    [Fact]
    public void AddTwinCatAdsSimulation_CalledTwice_DoesNotDuplicateHostedServices()
    {
        // AddTwinCatAdsSimulation now registers the same core services as
        // AddTwinCatAds (router + pool = 2 hosted services).  Calling it twice
        // must not result in 4 hosted services — the AdsRouterReadySignal guard
        // in RegisterCoreServices ensures idempotency for the service registrations.
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions { Mode = ConnectionMode.Simulated };
        });
        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions { Mode = ConnectionMode.Simulated };
        });

        // Exactly 2 IHostedService registrations: one router + one pool (not 4).
        int hostedServiceCount = services
            .Count(d => d.ServiceType == typeof(IHostedService));

        Assert.Equal(2, hostedServiceCount);
    }

    [Fact]
    public void AddTwinCatAdsSimulation_CalledTwice_DoesNotDuplicateAdsRouterReadySignal()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions { Mode = ConnectionMode.Simulated };
        });
        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions { Mode = ConnectionMode.Simulated };
        });

        int readySignalCount = services
            .Count(d => d.ServiceType == typeof(AdsRouterReadySignal));

        Assert.Equal(1, readySignalCount);
    }

    // ------------------------------------------------------------------
    // Mixed-call idempotency: AddTwinCatAds then AddTwinCatAdsSimulation
    // ------------------------------------------------------------------

    [Fact]
    public void AddTwinCatAds_Then_AddTwinCatAdsSimulation_DoesNotDuplicateHostedServices()
    {
        // AddTwinCatAds registers core (router + pool).
        // AddTwinCatAdsSimulation then hits the guard (RegisterCoreServices skips)
        // and only registers the PostConfigure mode-flip.
        // Total hosted services: still 2 (not 4).
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddTwinCatAds(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
        });

        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions { Mode = ConnectionMode.Simulated };
        });

        int hostedServiceCount = services
            .Count(d => d.ServiceType == typeof(IHostedService));

        Assert.Equal(2, hostedServiceCount);
    }

    [Fact]
    public void AddTwinCatAds_Then_AddTwinCatAdsSimulation_SimPostConfigureFlipsAllModes()
    {
        // After core is registered by AddTwinCatAds, calling AddTwinCatAdsSimulation
        // must still register the PostConfigure (which is outside the guard) so
        // ALL targets — including those from the AddTwinCatAds call — are flipped
        // to Simulated mode.
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddTwinCatAds(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6", Mode = ConnectionMode.Real };
        });

        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions { Mode = ConnectionMode.Simulated };
        });

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        // The PostConfigure from AddTwinCatAdsSimulation flips BOTH targets.
        Assert.Equal(ConnectionMode.Simulated, opts.Targets["plc1"].Mode);
        Assert.Equal(ConnectionMode.Simulated, opts.Targets["sim1"].Mode);
    }
}
