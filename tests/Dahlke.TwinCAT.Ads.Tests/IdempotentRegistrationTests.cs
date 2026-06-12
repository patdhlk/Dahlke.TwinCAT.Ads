using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
    public void AddTwinCatAdsSimulation_CalledTwice_DoesNotDuplicateHostedService()
    {
        // Calling AddTwinCatAdsSimulation twice must not register two
        // SimulatedAdsConnectionPool hosted services.
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions { AmsNetId = "127.0.0.1.1.1" };
        });
        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions { AmsNetId = "127.0.0.1.1.1" };
        });

        int hostedServiceCount = services
            .Count(d => d.ServiceType == typeof(IHostedService));

        Assert.Equal(1, hostedServiceCount);
    }
}
