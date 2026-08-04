using Microsoft.Extensions.DependencyInjection;

namespace Dahlke.EtherCAT.Diagnostics;

/// <summary>Registration for EtherCAT diagnostics.</summary>
public static class EtherCatServiceCollectionExtensions
{
    /// <summary>
    /// Registers the EtherCAT client, snapshot cache and polling monitor, and starts the monitor as
    /// a hosted service.
    /// </summary>
    /// <param name="services">The collection to register into.</param>
    /// <param name="startMonitor">
    /// Whether to run the monitor as a hosted service. There is no internal enable flag, so this is
    /// how polling is turned off; the monitor is still registered, so anything that depends on
    /// <see cref="IEtherCatMonitor"/> keeps resolving. Pass <see langword="false"/> when the
    /// application exposes its EtherCAT surface unconditionally but only wants to poll under a
    /// feature flag — a REST controller is typically activated by its framework BEFORE whatever
    /// gate would have turned it away, so its dependencies have to resolve either way.
    /// </param>
    /// <remarks>
    /// The caller must also register an <see cref="IEtherCatDiagnosticsHandler"/> and an
    /// <see cref="IEtherCatOptionsSource"/>; both are application concerns this library cannot
    /// supply. Not calling this at all is the other way to turn EtherCAT diagnostics off, and the
    /// right one when nothing in the application references the library's services.
    /// </remarks>
    public static IServiceCollection AddEtherCatDiagnostics(
        this IServiceCollection services, bool startMonitor = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IEtherCatClient, EtherCatClient>();
        services.AddSingleton<IEtherCatCache, EtherCatCache>();

        // One instance serving three roles: the concrete type for the hosted-service registration,
        // and IEtherCatMonitor for callers that re-arm CRC notifications. Registering the concrete
        // type twice would give two monitors, one of them polling invisibly.
        services.AddSingleton<EtherCatMonitor>();
        services.AddSingleton<IEtherCatMonitor>(sp => sp.GetRequiredService<EtherCatMonitor>());

        if (startMonitor)
            services.AddHostedService(sp => sp.GetRequiredService<EtherCatMonitor>());

        return services;
    }
}
