using Dahlke.TwinCAT.Ads;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Dahlke.EtherCAT.Diagnostics.Tests;

/// <summary>
/// Pins <see cref="EtherCatServiceCollectionExtensions.AddEtherCatDiagnostics"/>'s two DI
/// invariants, both named in the method's own remarks and neither previously tested.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invariant 1.</b> The concrete <see cref="EtherCatMonitor"/>, the <see cref="IEtherCatMonitor"/>
/// resolution, and the registered <see cref="IHostedService"/> must all be the SAME instance.
/// Tidying the registration to the more conventional
/// <c>AddSingleton&lt;IEtherCatMonitor, EtherCatMonitor&gt;()</c> would silently give two monitor
/// instances instead of one serving three roles — one resolved through the interface (e.g. by
/// <c>EtherCatService.ClearCrcNotification</c>) and a SECOND, separate one started as the hosted
/// service and actually polling. The interface caller's re-arm would then clear a
/// <c>HashSet</c> nothing reads, while the polling instance's own CRC notification state stays
/// suppressed — making the CRC alarm one-shot per process and unrecoverable by the documented
/// REST reset, with every existing test still green because none of them resolve both the
/// concrete type and the interface from the SAME container and compare.
/// </para>
/// <para>
/// <b>Invariant 2.</b> With <c>startMonitor: false</c>, <see cref="IEtherCatMonitor"/> must still
/// resolve — so a REST controller that depends on it (to re-arm CRC notifications) can activate
/// even when the feature is disabled — but NO <see cref="IHostedService"/> may be registered. That
/// second half is what guarantees a disabled feature never opens a connection to a live machine;
/// a hosted service registered unconditionally would poll regardless of the flag.
/// </para>
/// <para>
/// Modelled on <c>Adsify.Tests.Integration.EsiCatalogLifetimeTests</c>, which pins the analogous
/// singleton-lifetime guarantee for <c>EsiCatalog</c>. That test resolves from adsify's own
/// <c>WebApplicationFactory</c> container; this one builds a minimal <see cref="IServiceCollection"/>
/// directly, because <c>AddEtherCatDiagnostics</c> is the library's own extension method and its
/// invariants hold (or fail) independent of any particular host.
/// </para>
/// </remarks>
public class EtherCatServiceCollectionExtensionsTests
{
    /// <summary>
    /// Everything <see cref="EtherCatMonitor"/>'s constructor needs to resolve, short of
    /// <see cref="IEtherCatClient"/> and <see cref="IEtherCatCache"/> themselves (which
    /// <c>AddEtherCatDiagnostics</c> registers). <see cref="IAdsRawChannelFactory"/> is a
    /// substitute rather than a real simulated factory: nothing here ever calls it, since these
    /// tests only resolve the monitor, never poll with it.
    /// </summary>
    private static ServiceProvider BuildProvider(bool startMonitor)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IAdsRawChannelFactory>());
        services.AddSingleton<IOptions<TwinCatAdsOptions>>(Options.Create(new TwinCatAdsOptions()));
        services.AddSingleton(Substitute.For<IEtherCatDiagnosticsHandler>());
        services.AddSingleton(Substitute.For<IEtherCatOptionsSource>());

        services.AddEtherCatDiagnostics(startMonitor);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Started_monitor_concrete_type_interface_and_hosted_service_are_the_same_instance()
    {
        using ServiceProvider provider = BuildProvider(startMonitor: true);

        var concrete = provider.GetRequiredService<EtherCatMonitor>();
        var asInterface = provider.GetRequiredService<IEtherCatMonitor>();
        var hosted = provider.GetServices<IHostedService>().Should().ContainSingle(
            "AddEtherCatDiagnostics(startMonitor: true) must register exactly one hosted service")
            .Subject;

        asInterface.Should().BeSameAs(concrete,
            "registering the concrete monitor a second time (e.g. AddSingleton<IEtherCatMonitor, " +
            "EtherCatMonitor>()) would give two monitors, one polling invisibly while the other " +
            "answers ClearCrcNotification calls that never reach the poller");
        hosted.Should().BeSameAs(concrete,
            "the instance the host STARTS must be the same one every other caller resolves through " +
            "IEtherCatMonitor, or the two drift apart silently");
    }

    [Fact]
    public void Disabled_monitor_still_resolves_through_the_interface_but_registers_no_hosted_service()
    {
        using ServiceProvider provider = BuildProvider(startMonitor: false);

        var monitor = provider.GetRequiredService<IEtherCatMonitor>();
        monitor.Should().NotBeNull(
            "a disabled feature must still resolve for callers like a REST controller's " +
            "ClearCrcNotification path, or MVC activation fails before the feature's own gate runs");

        provider.GetServices<IHostedService>().Should().BeEmpty(
            "this is the half of the feature flag that guarantees a disabled feature never polls " +
            "a live machine — registering the hosted service unconditionally would defeat it");
    }
}
