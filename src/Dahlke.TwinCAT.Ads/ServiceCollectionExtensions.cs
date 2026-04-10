using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dahlke.TwinCAT.Ads;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the embedded ADS router and connection pool with health checks
    /// and automatic reconnection.
    /// <para>Expects the "AmsRouter" and "PlcTargets" configuration sections in appsettings.json.</para>
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
    /// Registers a simulated PLC connection for offline development.
    /// No ADS router or TwinCAT required.
    /// <para>Expects the "PlcTargets" configuration section in appsettings.json.</para>
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
