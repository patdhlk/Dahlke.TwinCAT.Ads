using Microsoft.Extensions.Configuration;
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

    [Fact]
    public void KeyedConnection_ExplicitKeyedRegistration_BeatsAnyKey()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
            o.Targets["plc2"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.7" };
        });

        // Substituting ONE target in a test is the reason this has to work.
        var stub = new StubConnection();
        services.AddKeyedSingleton<IAdsConnection>("plc1", stub);

        using var sp = services.BuildServiceProvider();

        Assert.Same(stub, sp.GetRequiredKeyedService<IAdsConnection>("plc1"));

        // ...and the substitution is surgical: plc2 still comes from the pool.
        Assert.Same(
            sp.GetRequiredService<IAdsConnectionPool>().GetConnection("plc2"),
            sp.GetRequiredKeyedService<IAdsConnection>("plc2"));
    }

    // Enumerating with the AnyKey sentinel is one of the few places where the DI container's
    // own behaviour differs across the target frameworks, so the assertion has to as well.
    // Either way pool.GetAllConnections() is the answer; what differs is how the container
    // says so.
#if NET10_0_OR_GREATER
    [Fact]
    public void KeyedConnection_EnumeratingWithAnyKey_ReturnsEmpty()
    {
        using var sp = BuildProvider();

        // .NET 10 skips AnyKey descriptors when enumerating, so the factory is never
        // reached and the caller simply gets nothing back.
        Assert.Empty(sp.GetKeyedServices<IAdsConnection>(KeyedService.AnyKey));
    }
#else
    [Fact]
    public void KeyedConnection_EnumeratingWithAnyKey_ThrowsPointingAtTheEnumerationApi()
    {
        using var sp = BuildProvider();

        // On .NET 8 and 9 GetKeyedServices(AnyKey) does NOT skip AnyKey descriptors — it
        // invokes the factory passing the sentinel itself. Enumeration cannot be served, so
        // this throws; what it must not do is throw naming AnyKeyObj, an internal DI type.
        var ex = Assert.Throws<InvalidOperationException>(
            () => sp.GetKeyedServices<IAdsConnection>(KeyedService.AnyKey).ToList());

        Assert.Contains("GetAllConnections", ex.Message);
        Assert.DoesNotContain("AnyKeyObj", ex.Message);
    }
#endif

    [Fact]
    public void UnkeyedIAdsConnection_IsStillNotRegistered()
    {
        using var sp = BuildProvider();

        // Deliberate: with a fleet there is no "the" connection, and a default would
        // silently pick one.
        Assert.Null(sp.GetService<IAdsConnection>());
    }

    [Fact]
    public void UnkeyedIAdsConnection_EnumerationIsEmpty_NotAnExplodingKeyedFactory()
    {
        using var sp = BuildProvider();

        // The path a consumer reaches by accident: injecting IEnumerable<IAdsConnection>.
        // The keyed descriptor must stay out of the UNKEYED enumeration, or a plausible
        // constructor signature would blow up on the AnyKey sentinel.
        Assert.Empty(sp.GetServices<IAdsConnection>());
    }

    [Fact]
    public void AddTwinCatAds_CalledTwice_RegistersOneKeyedConnectionDescriptor()
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

        var keyedCount = services.Count(d =>
            d.ServiceType == typeof(IAdsConnection) && d.IsKeyedService);

        Assert.Equal(1, keyedCount);
    }

    [Fact]
    public void KeyedConnection_ResolvesFromConfigurationFirstOverload()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlcTargets:plc1:AmsNetId"] = "1.2.3.4.5.6",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(configuration);

        using var sp = services.BuildServiceProvider();

        Assert.Equal("plc1", sp.GetRequiredKeyedService<IAdsConnection>("plc1").PlcId);
    }

    [Fact]
    public void KeyedConnection_ResolvesFromComboOverload()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlcTargets:fromConfig:AmsNetId"] = "1.2.3.4.5.6",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(configuration, o =>
        {
            o.Targets["fromLambda"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.7" };
        });

        using var sp = services.BuildServiceProvider();

        Assert.Equal("fromConfig", sp.GetRequiredKeyedService<IAdsConnection>("fromConfig").PlcId);
        Assert.Equal("fromLambda", sp.GetRequiredKeyedService<IAdsConnection>("fromLambda").PlcId);
    }

    [Fact]
    public void KeyedConnection_TargetAddedByLaterConfigureDelegate_IsResolvable()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
        });

        // Registered AFTER AddTwinCatAds returned. This is the case a per-target loop at
        // registration time could not have covered, and it is why the descriptor is AnyKey.
        services.Configure<TwinCatAdsOptions>(o =>
        {
            o.Targets["late"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.9" };
        });

        using var sp = services.BuildServiceProvider();

        Assert.Equal("late", sp.GetRequiredKeyedService<IAdsConnection>("late").PlcId);
    }

    [Fact]
    public void KeyedConnection_FromKeyedServicesAttribute_InjectsConfiguredTarget()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
            o.Targets["plc2"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.7" };
        });
        services.AddSingleton<TempService>();

        using var sp = services.BuildServiceProvider();

        // The shape from issue #43: the id appears once, at the injection point.
        var service = sp.GetRequiredService<TempService>();

        Assert.Equal("plc2", service.Connection.PlcId);
        Assert.Same(
            sp.GetRequiredService<IAdsConnectionPool>().GetConnection("plc2"),
            service.Connection);
    }

    private sealed class TempService([FromKeyedServices("plc2")] IAdsConnection connection)
    {
        public IAdsConnection Connection { get; } = connection;
    }

    /// <summary>
    /// A minimal hand-written double. Every unimplemented member of
    /// <see cref="AdsConnectionBase"/> throws, which is what we want: this stub exists to
    /// be compared by reference, never called.
    /// </summary>
    private sealed class StubConnection : AdsConnectionBase
    {
        public override string PlcId => "stub";
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
