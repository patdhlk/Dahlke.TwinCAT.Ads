using Microsoft.Extensions.DependencyInjection;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Tests for the keyed <see cref="IAdsConnection"/> registration added by every
/// <c>AddTwinCatAds</c> / <c>AddTwinCatAdsSimulation</c> overload, which lets a consumer
/// name a target once at the injection point rather than at every call site.
/// </summary>
public class KeyedConnectionRegistrationTests
{
    [Fact]
    public void KeyedConnection_ResolvesSameFacadeAsPool()
    {
        using var sp = BuildProvider();

        var pool = sp.GetRequiredService<IAdsConnectionPool>();
        var keyed = sp.GetRequiredKeyedService<IAdsConnection>("plc1");

        // Same instance, not a copy: a subscription taken through either one is on
        // the same facade and the same durable subscription registry.
        Assert.Same(pool.GetConnection("plc1"), keyed);
    }

    [Fact]
    public void KeyedConnection_KeyMatchingIsCaseInsensitive()
    {
        using var sp = BuildProvider();

        // "plc1" and "PLC1" are DIFFERENT container cache slots, so the factory runs
        // twice. Both answers being the same instance is what proves identity comes
        // from the pool's OrdinalIgnoreCase facade dictionary, not from DI caching.
        Assert.Same(
            sp.GetRequiredKeyedService<IAdsConnection>("plc1"),
            sp.GetRequiredKeyedService<IAdsConnection>("PLC1"));
    }

    [Fact]
    public void KeyedConnection_UnknownId_ThrowsListingConfiguredTargets()
    {
        using var sp = BuildProvider();

        var ex = Assert.Throws<UnknownPlcTargetException>(
            () => sp.GetRequiredKeyedService<IAdsConnection>("typo"));

        Assert.Equal("typo", ex.PlcId);
        Assert.Contains("plc1", ex.Message);
        Assert.Contains("plc2", ex.Message);
    }

    [Fact]
    public void KeyedConnection_UnknownId_OptionalResolutionAlsoThrows()
    {
        using var sp = BuildProvider();

        // Deliberate asymmetry against ordinary keyed DI, documented in the README:
        // the factory runs on the optional path too, so there is no null to return
        // before it throws.
        Assert.Throws<UnknownPlcTargetException>(
            () => sp.GetKeyedService<IAdsConnection>("typo"));
    }

    [Fact]
    public void KeyedConnection_NonStringKey_ThrowsNamingTheKeyType()
    {
        using var sp = BuildProvider();

        // AnyKey matches any non-null key, including ones the pool cannot take.
        var ex = Assert.Throws<UnknownPlcTargetException>(
            () => sp.GetRequiredKeyedService<IAdsConnection>(42));

        Assert.Contains("string service key", ex.Message);
        Assert.Contains("Int32", ex.Message);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
            o.Targets["plc2"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.7" };
        });
        return services.BuildServiceProvider();
    }
}
