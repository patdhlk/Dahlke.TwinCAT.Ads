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
///   behaviour; reads targets, router, diagnostics and raw-channel settings from
///   the supplied configuration.</item>
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
/// appends a <c>PostConfigure&lt;TOptions&gt;</c> delegate
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
    /// (the <c>PlcTargets</c>, <c>AmsRouter</c>, <c>AdsSymbolDump</c> and
    /// <c>RawChannels</c> sections; each is optional).</para>
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
    /// Reads options from the supplied <paramref name="configuration"/> exactly as
    /// <see cref="AddTwinCatAds(IServiceCollection,IConfiguration)"/> does, then
    /// applies a PostConfigure delegate that sets every target's
    /// <see cref="PlcTargetOptions.Mode"/> — and
    /// <see cref="AdsRawChannelOptions.Mode"/> — to
    /// <see cref="ConnectionMode.Simulated"/> after all other Configure delegates
    /// have run.
    /// </para>
    /// <para>
    /// <b>That PostConfigure BEATS configuration.</b> Binding is a Configure
    /// delegate and therefore runs first, so a host that writes
    /// <c>"RawChannels": { "Mode": "Real" }</c> and calls this method still gets
    /// simulation — which is the point, since this overload's whole promise is that
    /// no hardware is needed.
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
        RegisterCoreOptionsValidator(services);

        services.AddOptions<TwinCatAdsOptions>()
            .Configure(o =>
            {
                // Targets ← PlcTargets section (existing layout, unchanged).
                var plcTargets = configuration.GetSection("PlcTargets");
                plcTargets.Bind(o.Targets);

                // InitialValues ← re-read from the section so declared-type seed entries
                // ({ "value": 1500, "type": "DINT" }) keep a PLC-faithful CLR type. The stock
                // binder cannot: configuration is string-typed and a nested entry has no target
                // type to bind onto. See InitialValueBinder.
                InitialValueBinder.Bind(plcTargets, o.Targets);

                // Router ← AmsRouter:NetId and AmsRouter:Routes (existing layout).
                //
                // TWO PROPERTIES, not the section. A third property added to
                // AmsRouterOptions is NOT picked up here and would stay at its default
                // however a host spells it in configuration — the same way RawChannels
                // shipped dead. OptionsSectionsAreBoundTests does not cover this: it
                // guards new sections on TwinCatAdsOptions, not new members of a section
                // bound property-by-property. Extend these lines, or switch to a
                // whole-section Bind as RawChannels below does.
                var amsRouter = configuration.GetSection("AmsRouter");
                o.Router.NetId = amsRouter.GetValue<string>("NetId");

                // Routes is ASSIGNED, not Bind-appended, so a host that registers
                // twice ends up with one copy of each route rather than two. That
                // matters more here than for RawChannels:Seed: routes are keyed by
                // name and a duplicate name is a startup FAILURE, so appending would
                // break every host calling AddTwinCatAds twice.
                var routesSection = amsRouter.GetSection("Routes");
                var routes = routesSection.Get<List<AmsRouteOptions>>();
                if (routes is not null)
                    o.Router.Routes = routes;
                RecordDiscardedRoutes(routesSection, o.Router);

                // SymbolDump: bind legacy key first (lower precedence), then
                // new section over it (higher precedence wins).
                var legacyEnabled = configuration.GetValue<bool?>("AdsSymbolTreeDump");
                if (legacyEnabled is true)
                    o.Diagnostics.SymbolDump.Enabled = true;

                var symbolDumpSection = configuration.GetSection("AdsSymbolDump");
                if (symbolDumpSection.Exists())
                    symbolDumpSection.Bind(o.Diagnostics.SymbolDump);

                // RawChannels ← the whole section. Unlike the sections above there is
                // no legacy layout to reconcile, so a plain Bind covers every member.
                // Seed is an ARRAY of objects precisely so this works: a dictionary
                // keyed on "amsNetId:port" flattened into nested sections, because ':'
                // is the hierarchy separator, and bound with no slots at all.
                var rawChannels = configuration.GetSection("RawChannels");
                rawChannels.Bind(o.RawChannels);
                RecordDiscardedSeedEntries(rawChannels, o.RawChannels);
            })
            .ValidateOnStart();
    }

    /// <summary>
    /// Detects seed entries the binder silently DISCARDED and records them for the
    /// validator to report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ConfigurationBinder</c> swallows a failure inside a collection element and
    /// drops the element, so
    /// <c>"Seed": [{ "AmsNetId": "1.2.3.4.5.6", "Port": "typo" }]</c> binds to an
    /// empty list with no error — a configured seed that simply is not there. A bad
    /// SCALAR throws instead, so only collections need this.
    /// </para>
    /// <para>
    /// <b>Both levels are checked, because BOTH are reachable.</b> A slot is itself a
    /// collection element, so it is dropped by the same mechanism while the outer
    /// counts still agree. Two routes reach it, and neither needs a convertible-typed
    /// member:
    /// </para>
    /// <list type="bullet">
    ///   <item>a failed CONVERSION — <c>Port</c> is the only convertible-typed member
    ///   today, which is why the entry-level message names it;</item>
    ///   <item>an element written as a SCALAR where an object belongs —
    ///   <c>"Slots": [ "0x11", "0x12" ]</c>, plausible from someone writing slots as
    ///   bare index groups. Measured: 2 configured, 0 bound, and without this check
    ///   zero errors, leaving the channel reachable but empty so every read answers an
    ///   ADS error.</item>
    /// </list>
    /// <para>
    /// A count comparison rather than a re-validation of each value, so it cannot drift
    /// from the binder's own rules: it fires for any cause of a drop, present or future.
    /// </para>
    /// <para>
    /// Deliberately <c>bound &lt; configured</c> rather than <c>!=</c>. A host that
    /// calls <c>AddTwinCatAds(configuration)</c> twice registers this delegate twice,
    /// and <c>Bind</c> APPENDS to a list, so the bound count legitimately exceeds the
    /// configured one. Only a SHORTFALL means something was thrown away.
    /// </para>
    /// <para>
    /// The slot pass runs only when the outer counts are EQUAL. That is what makes
    /// positional alignment sound — a dropped entry would shift every later index — and
    /// it also covers the double-registration case for free, since the second pass sees
    /// a doubled outer count and skips. A slot shortfall is therefore recorded exactly
    /// once.
    /// </para>
    /// <para>
    /// <b>Deliberately does NOT clear <see cref="AdsRawChannelOptions.SeedBindingErrors"/>
    /// first</b>, unlike <c>InitialValueBinder.Bind</c>, which recomputes its errors
    /// from scratch on every pass. This check cannot: on a second registration pass the
    /// bound count is already doubled, so the comparison is no longer meaningful and
    /// the pass stays silent. Clearing would then ERASE a shortfall the first pass
    /// correctly found — turning a cosmetic duplicate line into a silent miss. The
    /// duplicate is the lesser fault, and it only occurs for a double-registering host
    /// whose every entry is bad.
    /// </para>
    /// </remarks>
    private static void RecordDiscardedSeedEntries(
        IConfiguration rawChannels,
        AdsRawChannelOptions options)
    {
        var seedSection = rawChannels.GetSection("Seed");
        if (!seedSection.Exists())
            return;

        // The same enumeration the binder itself walked, so index i here is the entry
        // the binder appended at position i.
        var configuredEntries = seedSection.GetChildren().ToList();

        if (options.Seed.Count < configuredEntries.Count)
        {
            options.SeedBindingErrors.Add(
                $"RawChannels:Seed declares {configuredEntries.Count} " +
                $"entr{(configuredEntries.Count == 1 ? "y" : "ies")} but only {options.Seed.Count} " +
                $"could be bound; the configuration binder DISCARDED the rest instead of reporting " +
                $"them. Check that every entry is an OBJECT and that its 'Port' is a number — an " +
                $"entry the binder cannot bind is dropped silently.");

            // Indices no longer line up, so a slot comparison would misattribute.
            return;
        }

        if (options.Seed.Count != configuredEntries.Count)
            return;   // more bound than configured: a second registration pass appended.

        for (var i = 0; i < configuredEntries.Count; i++)
        {
            var configuredSlots = configuredEntries[i].GetSection("Slots").GetChildren().Count();
            var boundSlots = options.Seed[i].Slots.Count;

            if (boundSlots >= configuredSlots)
                continue;

            options.SeedBindingErrors.Add(
                $"RawChannels:Seed:{i}:Slots declares {configuredSlots} " +
                $"slot{(configuredSlots == 1 ? "" : "s")} but only {boundSlots} could be bound; the " +
                $"configuration binder DISCARDED the rest instead of reporting them. Each slot must " +
                $"be an OBJECT with 'IndexGroup', 'IndexOffset' and 'Bytes' — a bare value such as " +
                $"\"0x11\" cannot bind to a slot and is dropped silently, leaving the target " +
                $"reachable but unseeded.");
        }
    }

    /// <summary>
    /// Detects <c>AmsRouter:Routes</c> entries the binder silently DISCARDED and
    /// records them for the validator to report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same mechanism as <see cref="RecordDiscardedSeedEntries"/>, for the same
    /// reason: <c>ConfigurationBinder</c> swallows a failure inside a collection
    /// element and drops the element. No route member is convertible-typed — all three
    /// are <see cref="string"/> — but a conversion failure is not the only route to a
    /// drop. An element written as a SCALAR where an object belongs,
    /// <c>"Routes": [ "rack" ]</c>, cannot bind to a complex type and is dropped with
    /// no error at all. A route that vanishes is precisely the failure this section
    /// exists to remove: the host starts, the router runs, and every operation against
    /// the device answers <c>TargetMachineNotFound</c>.
    /// </para>
    /// <para>
    /// <b>Clears first, unlike the seed check.</b> <c>Routes</c> is ASSIGNED by the
    /// binding step rather than Bind-appended, so a second registration pass sees the
    /// same counts as the first and recomputes the same answer — the recompute is
    /// meaningful, so a stale message from an earlier pass should not survive
    /// alongside it. The seed check cannot clear, because its bound count doubles on a
    /// second pass and clearing would erase a real shortfall.
    /// </para>
    /// </remarks>
    private static void RecordDiscardedRoutes(
        IConfigurationSection routesSection,
        AmsRouterOptions options)
    {
        options.RouteBindingErrors.Clear();

        if (!routesSection.Exists())
            return;

        var configured = routesSection.GetChildren().Count();

        if (options.Routes.Count >= configured)
            return;

        options.RouteBindingErrors.Add(
            $"AmsRouter:Routes declares {configured} " +
            $"rout{(configured == 1 ? "e" : "es")} but only {options.Routes.Count} could be bound; " +
            $"the configuration binder DISCARDED the rest instead of reporting them. Each route " +
            $"must be an OBJECT with 'Name', 'NetId' and 'Address' — a bare value such as " +
            $"\"rack\" cannot bind to a route and is dropped silently, leaving the target " +
            $"unreachable with no error at startup.");
    }

    /// <summary>
    /// Registers validator + a pure code-first Configure delegate.
    /// Called by the code-first overloads that take no <see cref="IConfiguration"/>.
    /// </summary>
    private static void RegisterCodeFirstOptions(
        IServiceCollection services,
        Action<TwinCatAdsOptions> configure)
    {
        RegisterCoreOptionsValidator(services);

        services.AddOptions<TwinCatAdsOptions>()
            .Configure(configure)
            .ValidateOnStart();
    }

    /// <summary>
    /// Adds <see cref="TwinCatAdsOptionsValidator"/> to the set of validators the options
    /// infrastructure runs, without disturbing any others.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>TryAddEnumerable, not TryAddSingleton.</b> <c>TryAddSingleton</c> adds only when no
    /// descriptor for the service type exists AT ALL, so a consumer who registered any
    /// <see cref="IValidateOptions{TOptions}"/> of their own before calling <c>AddTwinCatAds</c>
    /// did not add a rule beside ours — they silently replaced every one of them. Adding a
    /// validator is the natural way to add a rule, and it was the exact gesture that removed
    /// the AmsNetId, duplicate-Net-ID, timeout and router checks. Nothing warned; the first
    /// sign was a runtime connection failure pointing at the network rather than at the
    /// configuration that validation existed to reject.
    /// </para>
    /// <para>
    /// <c>TryAddEnumerable</c> dedupes on (ServiceType, ImplementationType), so the two call
    /// sites cannot double-register when a host composes a config-bound registration with a
    /// code-first one, and repeat <c>AddTwinCatAds</c> calls stay idempotent. The options
    /// infrastructure consumes <c>IEnumerable&lt;IValidateOptions&lt;T&gt;&gt;</c> and
    /// concatenates every validator's failures, so a consumer's rules and ours now both report
    /// in one startup failure.
    /// </para>
    /// </remarks>
    private static void RegisterCoreOptionsValidator(IServiceCollection services) =>
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<TwinCatAdsOptions>, TwinCatAdsOptionsValidator>());

    /// <summary>
    /// Registers the core real-hardware services shared by all
    /// <c>AddTwinCatAds</c> overloads: <see cref="TimeProvider"/>, the router
    /// ready signal, <see cref="AdsRouterService"/>, the connection factory, and
    /// the connection pool (both as <see cref="AdsConnectionPool"/> and as
    /// <see cref="IAdsConnectionPool"/>), a keyed <see cref="IAdsConnection"/> resolvable
    /// by target identifier, and the raw channel factory (both as
    /// <see cref="AdsRawChannelFactory"/> and as <see cref="IAdsRawChannelFactory"/>).
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

        // Keyed IAdsConnection, so a service that talks to ONE target names it at the
        // injection point — [FromKeyedServices("plc1")] IAdsConnection — instead of
        // repeating the id at every pool lookup.
        //
        // AnyKey rather than a descriptor per configured target, because there are no
        // configured targets to loop over HERE. TwinCatAdsOptions is bound when
        // IOptions<T> is first resolved — which is AdsConnectionPool's constructor, long
        // after this method returns. Materializing options early would mean running a
        // code-first caller's Action<TwinCatAdsOptions> a second time against a throwaway
        // instance (side effects twice), and would still miss any target added by a
        // Configure delegate registered after AddTwinCatAds returns — silently, as an
        // unresolvable service rather than a startup error.
        //
        // Singleton is safe ONLY because AdsConnectionFacade is not IDisposable: the
        // container caches what this factory returns and would dispose it at shutdown,
        // racing the pool, which is the facade's real owner. If the facade ever becomes
        // disposable, this registration has to change with it.
        services.AddKeyedSingleton<IAdsConnection>(KeyedService.AnyKey, static (sp, key) =>
            sp.GetRequiredService<IAdsConnectionPool>().GetConnection(AsPlcId(key)));

        services.AddSingleton<AdsRawChannelFactory>();
        services.AddSingleton<IAdsRawChannelFactory>(sp => sp.GetRequiredService<AdsRawChannelFactory>());
        services.AddHostedService(sp => sp.GetRequiredService<AdsRawChannelFactory>());
    }

    /// <summary>
    /// Converts a keyed-service key into a PLC target id, rejecting the keys
    /// <see cref="KeyedService.AnyKey"/> lets through that the pool cannot take.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AnyKey matches ANY non-null key, so <c>GetRequiredKeyedService&lt;IAdsConnection&gt;(42)</c>
    /// reaches the factory. Casting there would surface as an
    /// <see cref="InvalidCastException"/> naming neither the library nor what was wrong;
    /// this reuses <see cref="UnknownPlcTargetException"/> so that both ways of LOOKING UP a
    /// connection that is not there fail with one exception type.
    /// </para>
    /// <para>
    /// <b>On .NET 8 and 9 the sentinel itself arrives here too.</b>
    /// <c>GetKeyedServices&lt;IAdsConnection&gt;(KeyedService.AnyKey)</c> does not skip AnyKey
    /// descriptors as one might expect — it invokes this factory passing
    /// <see cref="KeyedService.AnyKey"/> as the key. Left to the general path that produces a
    /// message naming <c>AnyKeyObj</c>, an internal type of the DI container that tells the
    /// caller nothing. Enumeration cannot be served — there is no single connection for "any
    /// key" — so it fails, but it fails naming the API that CAN serve it.
    /// </para>
    /// <para>
    /// .NET 10 changed that, and the sentinel branch is DEAD there: enumeration skips AnyKey
    /// descriptors (the caller gets an empty sequence), and passing the sentinel to
    /// <c>GetRequiredKeyedService</c> is rejected by the container itself with
    /// "KeyedService.AnyKey cannot be used to resolve a single service" before this factory runs.
    /// The branch stays because net8.0 and net9.0 are supported target frameworks and reach it on
    /// both paths.
    /// </para>
    /// </remarks>
    private static string AsPlcId(object? key)
    {
        if (key is string plcId)
            return plcId;

        if (ReferenceEquals(key, KeyedService.AnyKey))
            throw new InvalidOperationException(
                "Keyed IAdsConnection registrations cannot be enumerated: "
                + "GetKeyedServices<IAdsConnection>(KeyedService.AnyKey) reaches the factory with the "
                + "AnyKey sentinel itself, which names no PLC target. Use "
                + "IAdsConnectionPool.GetAllConnections() to walk every configured target, or "
                + "GetRequiredKeyedService<IAdsConnection>(\"plc1\") to resolve one by id.");

        throw new UnknownPlcTargetException(
            key?.ToString() ?? "(null)",
            $"A keyed IAdsConnection must be resolved with a string service key naming a configured "
            + $"PLC target; the key supplied was {key?.GetType().Name ?? "null"}.", null);
    }

    /// <summary>
    /// Registers the PostConfigure delegate used by all
    /// <c>AddTwinCatAdsSimulation</c> overloads.
    /// <para>
    /// The delegate flips every target's <see cref="PlcTargetOptions.Mode"/>, and
    /// <see cref="AdsRawChannelOptions.Mode"/>, to
    /// <see cref="ConnectionMode.Simulated"/> after all other Configure delegates
    /// have run, ensuring that config-bound or lambda-added targets are all in
    /// simulation mode regardless of how they were originally declared. Binding is
    /// itself a Configure delegate, so this deliberately overrides a
    /// <c>RawChannels:Mode</c> of <c>Real</c> read from configuration.
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

            // Raw channels too: otherwise the helper whose entire promise is
            // "no hardware needed" quietly starts an embedded router.
            o.RawChannels.Mode = ConnectionMode.Simulated;
        });
    }
}
