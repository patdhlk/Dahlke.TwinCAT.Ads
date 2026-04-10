using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dahlke.TwinCAT.Ads;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registriert den eingebetteten ADS-Router und den Verbindungspool mit Health-Checks
    /// und automatischer Reconnection.
    /// <para>Erwartet die Konfigurationssektionen "AmsRouter" und "PlcTargets" in appsettings.json.</para>
    /// </summary>
    public static IServiceCollection AddTwinCatAds(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<Dictionary<string, PlcTargetOptions>>(
            configuration.GetSection("PlcTargets"));

        services.AddSingleton<AdsRouterReadySignal>();
        services.AddHostedService<AdsRouterService>();
        services.AddSingleton<IAdsConnectionFactory, AdsConnectionFactory>();
        services.AddSingleton<AdsConnectionPool>();
        services.AddSingleton<IAdsConnectionPool>(sp => sp.GetRequiredService<AdsConnectionPool>());
        services.AddHostedService(sp => sp.GetRequiredService<AdsConnectionPool>());

        return services;
    }

    /// <summary>
    /// Registriert eine simulierte PLC-Verbindung für Offline-Entwicklung.
    /// Kein ADS-Router oder TwinCAT erforderlich.
    /// <para>Erwartet die Konfigurationssektion "PlcTargets" in appsettings.json.</para>
    /// </summary>
    public static IServiceCollection AddTwinCatAdsSimulation(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<Dictionary<string, PlcTargetOptions>>(
            configuration.GetSection("PlcTargets"));

        services.AddSingleton<SimulatedAdsConnectionPool>();
        services.AddSingleton<IAdsConnectionPool>(sp => sp.GetRequiredService<SimulatedAdsConnectionPool>());
        services.AddHostedService(sp => sp.GetRequiredService<SimulatedAdsConnectionPool>());

        return services;
    }
}
