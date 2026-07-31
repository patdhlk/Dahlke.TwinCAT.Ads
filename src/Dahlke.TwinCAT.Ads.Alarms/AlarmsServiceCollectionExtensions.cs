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
    /// the built-in JSON catalog, or your own <see cref="IPlcAlarmDialect"/> to speak a PLC
    /// alarm implementation other than <c>FB_ErrorHandler</c>.
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

            if (string.IsNullOrWhiteSpace(options.TextCatalog))
                return NullAlarmTextCatalog.Instance;

            // GetService, not GetRequiredService: a plain ServiceCollection with no host
            // has no IHostEnvironment, and that must keep working.
            return new JsonAlarmTextCatalog(
                ResolveCatalogPath(options.TextCatalog, sp.GetService<IHostEnvironment>()),
                sp.GetService<ILogger<JsonAlarmTextCatalog>>());
        });

        // TryAdd, so a consumer who registered their own dialect BEFORE this call keeps it —
        // which is what IPlcAlarmDialect's own documentation promises. The shipped default
        // speaks FB_ErrorHandler; anything else needs one of these.
        services.TryAddSingleton<IPlcAlarmDialect, ErrorHandlerAlarmDialect>();

        services.TryAddSingleton<PlcAlarmMonitor>();
        services.TryAddSingleton<IPlcAlarmMonitor>(sp => sp.GetRequiredService<PlcAlarmMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<PlcAlarmMonitor>());

        return services;
    }

    /// <summary>
    /// Anchors a relative <see cref="PlcAlarmsOptions.TextCatalog"/> to the host's content
    /// root, leaving an absolute path exactly as configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="JsonAlarmTextCatalog"/> opens the path it is given, so an unresolved
    /// relative path is interpreted against the PROCESS working directory. That is the same
    /// directory as the content root under <c>dotnet run</c> and almost never is for a
    /// published or service-hosted app, which turns the most natural configuration —
    /// <c>"TextCatalog": "alarms.json"</c> next to <c>appsettings.json</c> — into a startup
    /// <see cref="FileNotFoundException"/> on deployment and nowhere else. Anchoring here
    /// makes the two agree.
    /// </para>
    /// <para>
    /// An absolute path passes through untouched: it is an explicit instruction, and a
    /// deployment that names a catalog outside the content root means it.
    /// </para>
    /// </remarks>
    /// <param name="textCatalog">The configured path; already known to be non-blank.</param>
    /// <param name="environment">
    /// The host environment, or <see langword="null"/> when the container has none — a plain
    /// <see cref="ServiceCollection"/> built without a host. Then there is no content root to
    /// resolve against and the path is used as written.
    /// </param>
    internal static string ResolveCatalogPath(string textCatalog, IHostEnvironment? environment)
    {
        if (environment is null ||
            Path.IsPathRooted(textCatalog) ||
            string.IsNullOrWhiteSpace(environment.ContentRootPath))
        {
            return textCatalog;
        }

        return Path.Combine(environment.ContentRootPath, textCatalog);
    }
}
