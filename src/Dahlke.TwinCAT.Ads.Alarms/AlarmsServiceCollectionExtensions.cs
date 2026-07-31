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
    /// alarm implementation other than <c>FB_ErrorHandler</c>. A dialect registered before this
    /// call also suppresses the built-in dialect's options validation, which exists to check
    /// configuration only that dialect reads.
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

        // Scanned BEFORE anything below registers a dialect, so the built-in dialect and the
        // validator for ITS configuration go in as one unit or not at all.
        //
        // !d.IsKeyedService: PlcAlarmMonitor resolves this seam unkeyed, so a keyed registration
        // has not claimed it — the container would still have no unkeyed IPlcAlarmDialect to
        // give it, and fail to build with a message that points at this library, not at the
        // consumer's registration. Counting keyed descriptors here regressed that failure
        // against 0.7.0, whose TryAddSingleton compared ServiceKey and so left it alone.
        var dialectAlreadyRegistered =
            services.Any(d => d.ServiceType == typeof(IPlcAlarmDialect) && !d.IsKeyedService);

        // TryAddEnumerable, not TryAddSingleton: the latter adds only when NO descriptor for
        // IValidateOptions<PlcAlarmsOptions> exists at all, so a consumer registering any
        // validator of their own silently suppressed every built-in rule — blank SymbolPath,
        // unknown plcId, the lot — instead of adding to them. Validators are meant to compose,
        // and this package now relies on that: two of them share the work.
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<PlcAlarmsOptions>, PlcAlarmsOptionsValidator>());

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

        // A consumer who registered their own dialect BEFORE this call keeps it — which is what
        // IPlcAlarmDialect's own documentation promises. The shipped default speaks
        // FB_ErrorHandler; anything else needs one of these.
        //
        // Its validator is registered here and nowhere else, so the rules travel with the
        // dialect that reads them. A custom dialect brings its own IValidateOptions, or none:
        // 0.7.0 applied FB_ErrorHandler's acknowledge rules to every dialect, because validation
        // could not see which one the container would resolve (#25).
        if (!dialectAlreadyRegistered)
        {
            services.AddSingleton<IPlcAlarmDialect, ErrorHandlerAlarmDialect>();

            services.TryAddEnumerable(ServiceDescriptor
                .Singleton<IValidateOptions<PlcAlarmsOptions>,
                           ErrorHandlerAlarmDialectOptionsValidator>());
        }

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
