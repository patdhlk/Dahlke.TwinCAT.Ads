using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        ConfigureTwinCatAdsOptions(services, configuration);

        services.TryAddSingleton(TimeProvider.System);
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
        ConfigureTwinCatAdsOptions(services, configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SimulatedAdsConnectionPool>();
        services.AddSingleton<IAdsConnectionPool>(sp => sp.GetRequiredService<SimulatedAdsConnectionPool>());
        services.AddHostedService(sp => sp.GetRequiredService<SimulatedAdsConnectionPool>());

        return services;
    }

    /// <summary>
    /// Registers <see cref="TwinCatAdsOptions"/> and populates it from the
    /// existing configuration layout.  Called by both
    /// <see cref="AddTwinCatAds"/> and <see cref="AddTwinCatAdsSimulation"/>.
    /// </summary>
    private static void ConfigureTwinCatAdsOptions(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<TwinCatAdsOptions>()
            .Configure(o =>
            {
                // Targets ← PlcTargets section (existing layout, unchanged).
                configuration.GetSection("PlcTargets").Bind(o.Targets);

                // Router.NetId ← AmsRouter:NetId (existing layout).
                o.Router.NetId = configuration.GetSection("AmsRouter").GetValue<string>("NetId");

                // SymbolDump: bind legacy key first (lower precedence), then
                // new section over it (higher precedence wins).
                var legacyEnabled = configuration.GetValue<bool?>("AdsSymbolTreeDump");
                if (legacyEnabled is true)
                    o.Diagnostics.SymbolDump.Enabled = true;

                var symbolDumpSection = configuration.GetSection("AdsSymbolDump");
                if (symbolDumpSection.Exists())
                    symbolDumpSection.Bind(o.Diagnostics.SymbolDump);
            });
    }
}
