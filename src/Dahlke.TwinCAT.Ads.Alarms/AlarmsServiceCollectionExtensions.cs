using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// Registration extensions for PLC alarm monitoring.
/// </summary>
public static class AlarmsServiceCollectionExtensions
{
    /// <summary>
    /// Registers alarm monitoring for every target configured under the <c>PlcAlarms</c>
    /// section.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AddTwinCatAds</c> (or <c>AddTwinCatAdsSimulation</c>) must be registered first —
    /// this composes the connection pool rather than creating one.
    /// </para>
    /// <para>
    /// Register your own <see cref="IAlarmTextCatalog"/> before calling this to override
    /// the built-in JSON catalog.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddTwinCatAds(builder.Configuration)
    ///     .AddTwinCatAdsAlarms(builder.Configuration);
    /// </code>
    /// </example>
    public static IServiceCollection AddTwinCatAdsAlarms(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<IValidateOptions<PlcAlarmsOptions>, PlcAlarmsOptionsValidator>();

        services.AddOptions<PlcAlarmsOptions>()
            .Configure(o =>
            {
                var section = configuration.GetSection("PlcAlarms");
                section.Bind(o);
            })
            .ValidateOnStart();

        services.TryAddSingleton<IAlarmTextCatalog>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PlcAlarmsOptions>>().Value;

            return string.IsNullOrWhiteSpace(options.TextCatalog)
                ? NullAlarmTextCatalog.Instance
                : new JsonAlarmTextCatalog(
                    options.TextCatalog,
                    sp.GetService<ILogger<JsonAlarmTextCatalog>>());
        });

        services.TryAddSingleton<PlcAlarmMonitor>();
        services.TryAddSingleton<IPlcAlarmMonitor>(sp => sp.GetRequiredService<PlcAlarmMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<PlcAlarmMonitor>());

        return services;
    }
}
