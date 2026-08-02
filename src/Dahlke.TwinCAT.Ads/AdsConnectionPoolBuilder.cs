using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Builds and starts a connection pool without a generic host — the supported entry point
/// for console tools, WPF and WinForms applications.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a face on the DI path, not a second implementation.</b> The builder stands
/// up a private <see cref="IServiceCollection"/>, calls the very same
/// <c>AddTwinCatAds</c> / <c>AddTwinCatAdsSimulation</c> a hosted application calls, and
/// starts what comes out. The two entry points therefore cannot drift, and anything added
/// to the registration later reaches standalone consumers with no second edit.
/// </para>
/// <para>
/// <b>Ordering.</b> <see cref="UseConfiguration"/> binds first; <see cref="Configure"/>
/// and <see cref="AddTarget"/> delegates then run in call order. This is exactly the
/// guarantee the combo overload
/// <c>AddTwinCatAds(IConfiguration, Action&lt;TwinCatAdsOptions&gt;)</c> documents, and it
/// is obtained by delegating to that overload rather than by re-deriving it.
/// </para>
/// <example>
/// <code>
/// await using var pool = await AdsConnectionPoolBuilder
///     .Create()
///     .AddTarget("plc1", o => { o.AmsNetId = "192.168.1.10.1.1"; o.Port = 851; })
///     .BuildAndStartAsync();
///
/// var conn = pool.GetConnection("plc1");
/// </code>
/// </example>
/// </remarks>
public sealed class AdsConnectionPoolBuilder
{
    private readonly bool _simulation;
    private readonly List<Action<TwinCatAdsOptions>> _optionsDelegates = [];
    private readonly List<Action<IServiceCollection>> _serviceDelegates = [];
    private IConfiguration? _configuration;
    private ILoggerFactory? _loggerFactory;

    private AdsConnectionPoolBuilder(bool simulation) => _simulation = simulation;

    /// <summary>
    /// Starts a builder for real targets — the equivalent of <c>AddTwinCatAds</c>.
    /// </summary>
    public static AdsConnectionPoolBuilder Create() => new(simulation: false);

    /// <summary>
    /// Starts a builder that forces every target into
    /// <see cref="ConnectionMode.Simulated"/> — the equivalent of
    /// <c>AddTwinCatAdsSimulation</c>. No TwinCAT installation is required, and a target
    /// declared <see cref="ConnectionMode.Real"/> is overridden rather than honoured.
    /// </summary>
    public static AdsConnectionPoolBuilder CreateSimulation() => new(simulation: true);

    /// <summary>
    /// Configures the target named <paramref name="plcId"/>, creating it if it does not
    /// exist yet. Calling this twice for one identifier composes; it does not replace.
    /// </summary>
    /// <param name="plcId">The target identifier, matched case-insensitively.</param>
    /// <param name="configure">Applied to that target's options.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="plcId"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public AdsConnectionPoolBuilder AddTarget(string plcId, Action<PlcTargetOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plcId);
        ArgumentNullException.ThrowIfNull(configure);

        return Configure(o =>
        {
            if (!o.Targets.TryGetValue(plcId, out var target))
                o.Targets[plcId] = target = new PlcTargetOptions();

            configure(target);
        });
    }

    /// <summary>
    /// Adds a delegate applied to the whole options tree — router settings, diagnostics,
    /// raw channels, or targets in bulk.
    /// </summary>
    /// <param name="configure">Applied after configuration binding, in call order.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public AdsConnectionPoolBuilder Configure(Action<TwinCatAdsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _optionsDelegates.Add(configure);
        return this;
    }

    /// <summary>
    /// Binds options from <paramref name="configuration"/> — the <c>PlcTargets</c>,
    /// <c>AmsRouter</c>, <c>AdsSymbolDump</c> and <c>RawChannels</c> sections — before any
    /// <see cref="Configure"/> or <see cref="AddTarget"/> delegate runs.
    /// </summary>
    /// <param name="configuration">The configuration to bind. Calling twice replaces.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    public AdsConnectionPoolBuilder UseConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
        return this;
    }

    /// <summary>
    /// Supplies the logger factory the pool, router and raw channels log through.
    /// </summary>
    /// <remarks>
    /// Without this — and without a <see cref="ConfigureServices"/> call that registers
    /// logging — the pool logs to <see cref="NullLoggerFactory"/> and a console app is
    /// silent. That is the right default for a tool whose own output is the point; opting
    /// in is one line either way.
    /// </remarks>
    /// <param name="loggerFactory">The factory to use. Wins over anything registered via
    /// <see cref="ConfigureServices"/>.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is null.</exception>
    public AdsConnectionPoolBuilder UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory = loggerFactory;
        return this;
    }

    /// <summary>
    /// Adds services to the private container — the seam for companion packages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An <see cref="IHostedService"/> registered here is started and stopped with
    /// everything else, which is what lets a console app call
    /// <c>AddTwinCatAdsAlarms</c> and resolve the monitor from
    /// <see cref="AdsConnectionPoolHandle.Services"/>. These delegates run BEFORE the
    /// library's own registrations for everything EXCEPT hosted-service start order — a
    /// logging stack registered here is picked up by the TryAdd defaults below, but any
    /// <see cref="IHostedService"/> registered here is moved to start AFTER router, pool
    /// and raw channels (see <see cref="BuildAndStartAsync"/>), so a companion package's
    /// hosted service can
    /// assume the pool it depends on is already connected by the time its
    /// <c>StartAsync</c> runs — exactly as it would calling
    /// <c>AddTwinCatAds(...).AddTwinCatAdsAlarms(...)</c> on a generic host, where the
    /// second call's hosted service naturally starts after the first's.
    /// </para>
    /// <para>
    /// <b>The private container is a hosted-service runner, not a host.</b> Startup and
    /// shutdown call <see cref="IHostedService.StartAsync"/> and
    /// <see cref="IHostedService.StopAsync"/> and nothing else. Concretely, four things a
    /// generic host would do are absent:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="IHostedLifecycleService"/>'s <c>StartingAsync</c>,
    ///     <c>StartedAsync</c>, <c>StoppingAsync</c> and <c>StoppedAsync</c> are never
    ///     invoked, even on a service that implements the interface.
    ///   </description></item>
    ///   <item><description>
    ///     <c>BackgroundServiceExceptionBehavior</c> is not honoured. A
    ///     <see cref="BackgroundService"/> whose <c>ExecuteAsync</c> faults after startup
    ///     faults only its own task; nothing here observes that task, so the application
    ///     keeps running where <c>StopHost</c> — the host default — would have brought it
    ///     down.
    ///   </description></item>
    ///   <item><description>
    ///     The provider is built with neither <c>ValidateScopes</c> nor
    ///     <c>ValidateOnBuild</c>, so a captive dependency or an unresolvable registration
    ///     surfaces at first resolve rather than at build.
    ///   </description></item>
    ///   <item><description>
    ///     Shutdown is unbounded: there is no equivalent of
    ///     <c>HostOptions.ShutdownTimeout</c>, so a <c>StopAsync</c> that hangs hangs
    ///     <see cref="AdsConnectionPoolHandle.DisposeAsync"/> with it.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The first two are unreachable through this library's own registrations: the pool
    /// and the raw-channel factory are plain <see cref="IHostedService"/> implementations
    /// with no lifecycle hooks, and <c>AdsRouterService</c> — the one
    /// <see cref="BackgroundService"/> among them — wraps the whole of its
    /// <c>ExecuteAsync</c> in a catch-all, so its task never faults. A service registered
    /// HERE can reach all four, which is why they are stated in one place rather than
    /// left to be discovered one at a time.
    /// </para>
    /// </remarks>
    /// <param name="configure">Applied to the service collection, in call order.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public AdsConnectionPoolBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _serviceDelegates.Add(configure);
        return this;
    }

    /// <summary>
    /// Builds the container, validates the options, and starts every hosted service.
    /// </summary>
    /// <param name="ct">Cancels startup.</param>
    /// <returns>
    /// A started pool the caller owns and must dispose. Simulated targets are already
    /// connected when this returns; real targets connect once the embedded router is
    /// ready — see <see cref="AdsConnectionExtensions.WaitForConnectedAsync"/>.
    /// </returns>
    /// <exception cref="OptionsValidationException">
    /// The configured options are invalid — a malformed AMS Net ID, a duplicate Net ID, a
    /// bad seed entry. Thrown here for exactly the reasons a hosted application would fail
    /// at startup.
    /// </exception>
    /// <remarks>
    /// On any failure the services that did start are stopped in reverse and the provider
    /// is disposed before the exception propagates, so a caller that catches owns nothing.
    /// </remarks>
    public async Task<AdsConnectionPoolHandle> BuildAndStartAsync(CancellationToken ct = default)
    {
        var services = new ServiceCollection();

        // The caller's services go in FIRST so the TryAdd defaults below defer to them.
        foreach (var configure in _serviceDelegates)
            configure(services);

        // Logging: silent unless asked for. TryAdd so a ConfigureServices AddLogging wins;
        // Logger<T> lives in Logging.Abstractions, so the open generic costs no reference
        // and no adapter — and it is what lets AddTwinCatAdsAlarms (ILogger<PlcAlarmMonitor>)
        // resolve.
        services.TryAddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.TryAdd(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(Logger<>)));

        // An explicit UseLoggerFactory beats both: last registration wins for a single resolve.
        if (_loggerFactory is not null)
            services.AddSingleton(_loggerFactory);

        // Snapshot any IHostedService the caller registered via ConfigureServices BEFORE the
        // library's own AddTwinCatAds.../AddTwinCatAdsSimulation... call below adds router,
        // pool and raw channels. Registering the caller's services first (above) is right for
        // TryAdd precedence, but it is WRONG for hosted-service START ORDER: left alone, a
        // caller's IHostedService — the alarm monitor AddTwinCatAdsAlarms registers — would
        // run its StartAsync before the pool it depends on even exists. On a generic host the
        // caller avoids that themselves by calling AddTwinCatAds(...) before
        // AddTwinCatAdsAlarms(...); here ConfigureServices always runs first, so that
        // ordering choice is not available to them. The descriptors captured here are moved
        // to the end, after the library's own, once the call below has run.
        var callerHostedServices = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .ToList();

        void ComposedConfigure(TwinCatAdsOptions o)
        {
            foreach (var configure in _optionsDelegates)
                configure(o);
        }

        if (_configuration is null)
        {
            if (_simulation)
                services.AddTwinCatAdsSimulation(ComposedConfigure);
            else
                services.AddTwinCatAds(ComposedConfigure);
        }
        else
        {
            if (_simulation)
                services.AddTwinCatAdsSimulation(_configuration, ComposedConfigure);
            else
                services.AddTwinCatAds(_configuration, ComposedConfigure);
        }

        // Move each snapshotted descriptor to the end, in its original relative order — a
        // stable move, not a re-sort — so router, pool and raw channels (just registered
        // above) start FIRST and any caller-registered hosted service starts only once the
        // pool it depends on is already connected.
        foreach (var descriptor in callerHostedServices)
        {
            services.Remove(descriptor);
            services.Add(descriptor);
        }

        var provider = services.BuildServiceProvider();
        var started = new List<IHostedService>();

        try
        {
            // What Host.StartAsync does, and the single easiest thing to omit here.
            // ValidateOnStart only QUEUES validation; IStartupValidator runs it. In THIS
            // codebase AdsConnectionPool and AdsRouterService both read IOptions<T>.Value
            // eagerly in their constructors, so a malformed AMS Net ID is already caught the
            // moment GetServices<IHostedService>() below materialises them, with or without
            // this line. It stays anyway: it is what Host.StartAsync does, and it is what
            // would catch a bad options type nothing else resolves eagerly, now or later.
            provider.GetService<IStartupValidator>()?.Validate();

            // Registration order is start order: router, pool, raw channels, THEN any
            // ConfigureServices-registered hosted service — moved to the end above.
            foreach (var hosted in provider.GetServices<IHostedService>())
            {
                await hosted.StartAsync(ct).ConfigureAwait(false);
                started.Add(hosted);
            }

            return new AdsConnectionPoolHandle(provider, started);
        }
        catch
        {
            for (var i = started.Count - 1; i >= 0; i--)
            {
                // The original failure is what the caller needs to see; a secondary
                // failure while unwinding must not replace it.
                try { await started[i].StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { /* deliberately swallowed — see above */ }
            }

            await provider.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
