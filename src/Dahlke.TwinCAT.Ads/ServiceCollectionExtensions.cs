using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Extension methods for registering Dahlke.TwinCAT.Ads services in an
/// <see cref="IServiceCollection"/>.
/// </summary>
/// <remarks>
/// Three registration patterns are supported for each variant
/// (<c>AddTwinCatAds</c> / <c>AddTwinCatAdsSimulation</c>):
/// <list type="number">
///   <item><b>Config-only</b> — <c>AddTwinCatAds(IConfiguration)</c>: existing
///   behaviour; reads targets and router settings from the supplied configuration.</item>
///   <item><b>Code-first</b> — <c>AddTwinCatAds(Action&lt;TwinCatAdsOptions&gt;)</c>:
///   no <see cref="IConfiguration"/> required; suitable for pure code-first
///   applications, unit tests, and worker services that do not use
///   Microsoft.Extensions.Configuration.</item>
///   <item><b>Combo</b> — <c>AddTwinCatAds(IConfiguration, Action&lt;TwinCatAdsOptions&gt;)</c>:
///   configuration binding runs first; the lambda then layers on top.
///   Registration order ensures the lambda's Configure delegate is executed
///   <em>after</em> the binding delegate so that mutations to list / dictionary
///   properties (e.g. <c>Prefixes.Add</c>) survive and are not cleared by a
///   subsequent <c>Bind</c> call.</item>
/// </list>
/// <para>
/// <c>AddTwinCatAdsSimulation</c> is sugar over <c>AddTwinCatAds</c>: it
/// registers the identical core services (router service, factory, pool) and
/// appends a <see cref="IServiceCollection.PostConfigure{TOptions}"/> delegate
/// that forces every target into <see cref="ConnectionMode.Simulated"/> after all
/// other <c>Configure</c> delegates have run.  The router service and pool detect
/// the all-simulated configuration and skip the router wait entirely, so no
/// TwinCAT installation is required.
/// </para>
/// </remarks>
public static class ServiceCollectionExtensions
{
    // =========================================================================
    // AddTwinCatAds
    // =========================================================================

    /// <summary>
    /// Registers the embedded ADS router and connection pool with health checks
    /// and automatic reconnection.
    /// <para>Reads options from the supplied <paramref name="configuration"/>
    /// (expects the <c>AmsRouter</c> and <c>PlcTargets</c> sections).</para>
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration root.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddTwinCatAds(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        BindTwinCatAdsOptions(services, configuration);
        RegisterCoreServices(services);
        return services;
    }

    /// <summary>
    /// Registers the embedded ADS router and connection pool with health checks
    /// and automatic reconnection, using a code-first options delegate.
    /// <para>No <see cref="IConfiguration"/> is required; suitable for
    /// applications and tests that do not use the Microsoft configuration
    /// infrastructure.</para>
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">
    /// A delegate that populates <see cref="TwinCatAdsOptions"/> directly.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddTwinCatAds(
        this IServiceCollection services,
        Action<TwinCatAdsOptions> configure)
    {
        RegisterCodeFirstOptions(services, configure);
        RegisterCoreServices(services);
        return services;
    }

    /// <summary>
    /// Registers the embedded ADS router and connection pool with health checks
    /// and automatic reconnection, combining configuration binding with a
    /// code-first options delegate.
    /// <para>
    /// Configuration binding executes first so that the <paramref name="configure"/>
    /// lambda always sees the fully-bound state and can safely append to or
    /// override any individual setting.  In particular, mutations to
    /// <see cref="System.Collections.Generic.List{T}"/> and
    /// <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/> properties
    /// (e.g. <c>o.Diagnostics.SymbolDump.Prefixes.Add("X")</c> or
    /// <c>o.Targets["plc2"] = …</c>) are preserved because the binding step
    /// precedes the lambda step.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration root.</param>
    /// <param name="configure">
    /// A delegate applied on top of the configuration-bound options.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddTwinCatAds(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<TwinCatAdsOptions> configure)
    {
        // Binding delegate registered first → runs first in the options pipeline.
        BindTwinCatAdsOptions(services, configuration);
        // Lambda delegate registered second → runs after binding; list/dict
        // mutations survive because no subsequent Bind call clears them.
        services.AddOptions<TwinCatAdsOptions>().Configure(configure);
        RegisterCoreServices(services);
        return services;
    }

    // =========================================================================
    // AddTwinCatAdsSimulation
    // =========================================================================

    /// <summary>
    /// Sugar over <see cref="AddTwinCatAds(IServiceCollection,IConfiguration)"/>
    /// that forces every target into simulation mode for offline development.
    /// No ADS router or TwinCAT installation is required.
    /// <para>
    /// Reads options from the supplied <paramref name="configuration"/>
    /// (expects the <c>PlcTargets</c> section), then applies a
    /// <see cref="IServiceCollection"/> PostConfigure delegate that sets every
    /// target's <see cref="PlcTargetOptions.Mode"/> to
    /// <see cref="ConnectionMode.Simulated"/> after all other Configure delegates
    /// have run.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration root.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddTwinCatAdsSimulation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        BindTwinCatAdsOptions(services, configuration);
        RegisterCoreServices(services);
        RegisterSimulationPostConfigure(services);
        return services;
    }

    /// <summary>
    /// Sugar over <see cref="AddTwinCatAds(IServiceCollection,Action{TwinCatAdsOptions})"/>
    /// that forces every target into simulation mode for offline development.
    /// No <see cref="IConfiguration"/> or TwinCAT installation is required.
    /// <para>
    /// Registers the same core services as <c>AddTwinCatAds</c> and appends a
    /// PostConfigure delegate that sets every target's
    /// <see cref="PlcTargetOptions.Mode"/> to
    /// <see cref="ConnectionMode.Simulated"/> after all other Configure delegates
    /// have run.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">
    /// A delegate that populates <see cref="TwinCatAdsOptions"/> directly.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddTwinCatAdsSimulation(
        this IServiceCollection services,
        Action<TwinCatAdsOptions> configure)
    {
        RegisterCodeFirstOptions(services, configure);
        RegisterCoreServices(services);
        RegisterSimulationPostConfigure(services);
        return services;
    }

    /// <summary>
    /// Sugar over
    /// <see cref="AddTwinCatAds(IServiceCollection,IConfiguration,Action{TwinCatAdsOptions})"/>
    /// that forces every target into simulation mode for offline development.
    /// <para>
    /// The ordering guarantee is identical to the real combo overload: binding
    /// runs first, the lambda runs after, and the PostConfigure mode-flip runs
    /// last (after all Configure delegates).
    /// </para>
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration root.</param>
    /// <param name="configure">
    /// A delegate applied on top of the configuration-bound options.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddTwinCatAdsSimulation(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<TwinCatAdsOptions> configure)
    {
        BindTwinCatAdsOptions(services, configuration);
        services.AddOptions<TwinCatAdsOptions>().Configure(configure);
        RegisterCoreServices(services);
        RegisterSimulationPostConfigure(services);
        return services;
    }

    // =========================================================================
    // Private helpers
    // =========================================================================

    /// <summary>
    /// Registers validator + the config-based Configure delegate.
    /// Called by the config-only and combo overloads.
    /// </summary>
    private static void BindTwinCatAdsOptions(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton<IValidateOptions<TwinCatAdsOptions>, TwinCatAdsOptionsValidator>();

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
            })
            .ValidateOnStart();
    }

    /// <summary>
    /// Registers validator + a pure code-first Configure delegate.
    /// Called by the code-first overloads that take no <see cref="IConfiguration"/>.
    /// </summary>
    private static void RegisterCodeFirstOptions(
        IServiceCollection services,
        Action<TwinCatAdsOptions> configure)
    {
        services.TryAddSingleton<IValidateOptions<TwinCatAdsOptions>, TwinCatAdsOptionsValidator>();

        services.AddOptions<TwinCatAdsOptions>()
            .Configure(configure)
            .ValidateOnStart();
    }

    /// <summary>
    /// Registers the core real-hardware services shared by all
    /// <c>AddTwinCatAds</c> overloads: <see cref="TimeProvider"/>, the router
    /// ready signal, <see cref="AdsRouterService"/>, the connection factory, and
    /// the connection pool (both as <see cref="AdsConnectionPool"/> and as
    /// <see cref="IAdsConnectionPool"/>).
    /// </summary>
    private static void RegisterCoreServices(IServiceCollection services)
    {
        // Idempotency guard: a second AddTwinCatAds call must not duplicate
        // the router/pool hosted services.
        if (services.Any(d => d.ServiceType == typeof(AdsRouterReadySignal)))
            return;

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AdsRouterReadySignal>();
        // Use a factory delegate so that IConfiguration — which is an OPTIONAL
        // constructor parameter — is resolved via GetService<T>() (returns null
        // when absent) rather than the open-generic AddHostedService<T>() path,
        // which ignores nullable annotations and throws InvalidOperationException
        // when IConfiguration is not registered (pure code-first scenario).
        services.AddHostedService(sp => new AdsRouterService(
            sp.GetRequiredService<IOptions<TwinCatAdsOptions>>(),
            sp.GetService<IConfiguration>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<AdsRouterReadySignal>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IAdsConnectionFactory, AdsConnectionFactory>();
        services.AddSingleton<AdsConnectionPool>();
        services.AddSingleton<IAdsConnectionPool>(sp => sp.GetRequiredService<AdsConnectionPool>());
        services.AddHostedService(sp => sp.GetRequiredService<AdsConnectionPool>());
    }

    /// <summary>
    /// Registers the PostConfigure delegate used by all
    /// <c>AddTwinCatAdsSimulation</c> overloads.
    /// <para>
    /// The delegate flips every target's <see cref="PlcTargetOptions.Mode"/> to
    /// <see cref="ConnectionMode.Simulated"/> after all other Configure delegates
    /// have run, ensuring that config-bound or lambda-added targets are all in
    /// simulation mode regardless of how they were originally declared.
    /// </para>
    /// <para>
    /// This method is intentionally NOT guarded by an idempotency check — the
    /// PostConfigure must always be registered, even when
    /// <see cref="RegisterCoreServices"/> was already called by a preceding
    /// <c>AddTwinCatAds</c> call (the core guard only skips service registrations,
    /// not option delegates).  Registering PostConfigure twice is harmless: the
    /// second application is idempotent (it re-sets Mode to Simulated).
    /// </para>
    /// </summary>
    private static void RegisterSimulationPostConfigure(IServiceCollection services)
    {
        services.PostConfigure<TwinCatAdsOptions>(o =>
        {
            foreach (var target in o.Targets.Values)
                target.Mode = ConnectionMode.Simulated;
        });
    }
}
