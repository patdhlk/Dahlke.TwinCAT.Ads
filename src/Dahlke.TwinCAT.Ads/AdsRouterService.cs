using Microsoft.Extensions.Options;
using TwinCAT.Ads;
using TwinCAT.Ads.TcpRouter;
using TwinCAT.Router;
using Route = TwinCAT.Ads.Configuration.Route;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Hosted service that manages the optional embedded AMS TCP/IP router.
/// When <see cref="AmsRouterOptions.NetId"/> is <see langword="null"/> or empty
/// the service exits immediately and the host is expected to have a system
/// TwinCAT router already running.
/// </summary>
/// <remarks>
/// <b>Retry semantics:</b>
/// <para>
/// Router construction and start are wrapped in a retry loop using the SAME
/// backoff pattern as the connection pool's loops (a 2-second delay doubling up
/// to a 30-second cap, paced by <see cref="TimeProvider"/>). A failed start is
/// therefore a <em>transient, retried</em> state rather than a terminal one:
/// while the service keeps retrying, the <see cref="AdsRouterReadySignal"/>
/// stays PENDING. This is safe because the pool no longer blocks its own startup
/// on the signal — it defers only its real-target loops behind the signal and
/// releases them once the router becomes ready (see <see cref="AdsConnectionPool"/>).
/// </para>
/// <para>
/// <see cref="AdsRouterReadySignal.SetReady"/> fires from the
/// <c>RouterStatus.Started</c> event hook on a successful attempt. The backoff
/// resets to its minimum after a successful start (mirroring the pool loops).
/// A router that started and then crashes mid-run re-enters the retry loop with
/// backoff to restore the transport; the signal is already Ready so the
/// <c>TrySet</c> is a no-op, and recovering in-flight connections is the pool
/// loops' own reconnect responsibility — the router restart only restores the
/// AMS/ADS transport.
/// </para>
/// <para>
/// With retry-forever there is no unrecoverable router failure in the normal
/// path, so <see cref="AdsRouterReadySignal.SetFailed"/> is reserved for the
/// purely defensive case of an exception escaping the retry loop itself.
/// <see cref="AdsRouterReadySignal.SetCancelled"/> resolves the signal on host
/// shutdown.
/// </para>
/// </remarks>
/// <remarks>
/// <b>AmsTcpIpRouter constructor strategy:</b>
/// <para>
/// The Beckhoff <c>AmsTcpIpRouter</c> exposes two relevant constructor families:
/// <list type="bullet">
///   <item>
///     <c>AmsTcpIpRouter(AmsNetId, ILoggerFactory)</c> — takes a typed AMS Net ID
///     directly; no <see cref="IConfiguration"/> required.
///   </item>
///   <item>
///     <c>AmsTcpIpRouter(IConfiguration, ILoggerFactory)</c> — reads the full
///     application configuration, allowing users to set Beckhoff-specific keys
///     such as <c>AmsRouter:TcpPort</c>.
///   </item>
/// </list>
/// </para>
/// <para>
/// This service uses a hybrid strategy so that both code-first applications
/// (which may not register <see cref="IConfiguration"/> at all) and
/// configuration-driven applications work correctly:
/// <list type="number">
///   <item>
///     When <see cref="IConfiguration"/> is available in DI <em>and</em>
///     <c>AmsRouter:NetId</c> is explicitly set inside that configuration, the
///     <c>(IConfiguration, ILoggerFactory)</c> overload is used (Strategy A).
///     This preserves access to every Beckhoff-specific router key in
///     appsettings.json (e.g. <c>AmsRouter:TcpPort</c>).
///   </item>
///   <item>
///     In all other cases — <see cref="IConfiguration"/> not registered, or
///     registered but without a non-empty <c>AmsRouter:NetId</c> value (the
///     typical Generic Host / ASP.NET Core code-first scenario) — the
///     <c>(AmsNetId, ILoggerFactory)</c> overload is used (Strategy B).
///     The Net ID comes from <see cref="AmsRouterOptions"/>; all other router
///     settings use Beckhoff's defaults.
///   </item>
/// </list>
/// </para>
/// <para>
/// The strategy decision is encapsulated in the pure static method
/// <see cref="UseConfigurationPassThrough"/> so it can be unit-tested
/// independently of the router construction.
/// </para>
/// <para>
/// <see cref="IConfiguration"/> is therefore an <em>optional</em> constructor
/// parameter.  The service is resolved from DI using a factory delegate that
/// calls <c>sp.GetService&lt;IConfiguration&gt;()</c> (nullable) rather than
/// <c>GetRequiredService</c>.
/// </para>
/// </remarks>
internal class AdsRouterService : BackgroundService
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AdsRouterService> _logger;
    private readonly AdsRouterReadySignal _readySignal;
    private readonly TimeProvider _timeProvider;
    private readonly string? _netId;
    private readonly bool _needsRouter;

    // Snapshot taken in the constructor alongside _netId, so the routes handed to a
    // router are the ones validated at startup and cannot change under a retry.
    private readonly IReadOnlyList<AmsRouteOptions> _routes;

    // IConfiguration is optional — null when the application does not register
    // it (pure code-first scenario).  When present the full application config is
    // forwarded to AmsTcpIpRouter so Beckhoff-specific keys (AmsRouter:TcpPort and
    // the rest) remain honoured. Remote ROUTES are NOT among them: four candidate
    // spellings were measured through this overload and all yielded zero routes, so
    // they come from AmsRouterOptions.Routes and are added explicitly below.
    private readonly IConfiguration? _configuration;

    // Same backoff envelope as the pool's connection loops: a 2-second minimum
    // doubling up to a 30-second cap, paced by TimeProvider so tests stay
    // deterministic under FakeTimeProvider.
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initialises a new instance of <see cref="AdsRouterService"/>.
    /// </summary>
    /// <param name="options">Resolved TwinCAT ADS options.</param>
    /// <param name="configuration">
    /// Optional application configuration root.  When <see langword="null"/>
    /// the <c>AmsTcpIpRouter(AmsNetId, ILoggerFactory)</c> overload is used
    /// and only the Net ID is forwarded to Beckhoff's router.
    /// </param>
    /// <param name="loggerFactory">Logger factory used for the embedded router.</param>
    /// <param name="readySignal">
    /// Signal set once the router is started (or skipped).
    /// </param>
    /// <param name="timeProvider">
    /// Clock used to pace the retry backoff between failed start attempts.
    /// </param>
    public AdsRouterService(
        IOptions<TwinCatAdsOptions> options,
        IConfiguration? configuration,
        ILoggerFactory loggerFactory,
        AdsRouterReadySignal readySignal,
        TimeProvider timeProvider)
    {
        var value = options.Value;
        _netId = value.Router.NetId;
        _routes = value.Router.Routes.ToArray();
        _needsRouter = NeedsRouter(value);
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AdsRouterService>();
        _readySignal = readySignal;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Resolves the ready signal when the host never lets <see cref="ExecuteAsync"/> run
    /// at all.</b> <see cref="ExecuteAsync"/> is written to resolve the signal on every exit,
    /// which was a complete guarantee for as long as <see cref="BackgroundService"/> always
    /// invoked it. It no longer does: from <c>Microsoft.Extensions.Hosting.Abstractions</c>
    /// 10.0.0, <c>StartAsync</c> short-circuits on an already-cancelled token and leaves
    /// <c>ExecuteTask</c> in the Canceled state without ever calling <c>ExecuteAsync</c>. The
    /// signal would then never be resolved, and every pool loop awaiting it would wait
    /// forever — a shutdown requested DURING startup, which is exactly when a host has least
    /// slack, would hang instead of unwinding.
    /// </para>
    /// <para>
    /// Checked in a <c>finally</c> rather than up front so a token cancelled while
    /// <c>base.StartAsync</c> is running is caught too, and <c>SetCancelled</c> is TrySet, so
    /// on a framework that DID run <see cref="ExecuteAsync"/> — every version below 10.0.0 —
    /// this is a no-op over the state that method already set.
    /// </para>
    /// </remarks>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
                _readySignal.SetCancelled();
        }
    }

    /// <summary>
    /// Whether the embedded AMS router must be started for this configuration.
    /// </summary>
    /// <remarks>
    /// Raw channels have no symbol layer and cannot route without the embedded
    /// router, so a host whose PLC targets are ALL simulated still needs one when
    /// <see cref="AdsRawChannelOptions.Mode"/> is <see cref="ConnectionMode.Real"/>.
    /// Before raw channels existed this was <c>_hasRealTargets</c>, which no
    /// longer describes the condition.
    /// </remarks>
    internal static bool NeedsRouter(TwinCatAdsOptions options) =>
        options.Targets.Values.Any(t => t.Mode == ConnectionMode.Real)
        || options.RawChannels.Mode == ConnectionMode.Real;

    /// <summary>
    /// Determines whether the <c>AmsTcpIpRouter(IConfiguration, ILoggerFactory)</c>
    /// constructor overload (Strategy A — config pass-through) should be used.
    /// </summary>
    /// <remarks>
    /// Strategy A is selected only when <paramref name="configuration"/> is
    /// non-null AND the <c>AmsRouter:NetId</c> key is present with a non-empty
    /// value.  This prevents the silent discard of a code-first
    /// <see cref="AmsRouterOptions.NetId"/> in Generic Host / ASP.NET Core apps
    /// where <see cref="IConfiguration"/> is always registered in DI — even
    /// when the developer never put <c>AmsRouter:NetId</c> in appsettings.json.
    ///
    /// Strategy B (<c>AmsTcpIpRouter(AmsNetId, ILoggerFactory)</c>) is used in
    /// all other cases: null configuration, configuration without the key, or
    /// configuration with an empty/whitespace value.
    /// </remarks>
    /// <param name="configuration">
    /// The optional application <see cref="IConfiguration"/> from DI; may be
    /// <see langword="null"/> in pure code-first scenarios.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the config pass-through overload should be
    /// used; <see langword="false"/> when the typed-NetId overload should be used.
    /// </returns>
    internal static bool UseConfigurationPassThrough(IConfiguration? configuration) =>
        configuration is not null &&
        !string.IsNullOrEmpty(configuration["AmsRouter:NetId"]);

    /// <inheritdoc />
    /// <remarks>
    /// The body runs a retry loop. The two "no router needed" branches
    /// resolve the signal as Ready immediately and return. Otherwise the
    /// per-attempt body (<see cref="RunRouterAttemptAsync"/>) is run inside a
    /// backoff loop:
    /// <list type="bullet">
    ///   <item>router started → <c>SetReady</c> fires from the status hook;</item>
    ///   <item>an attempt throws (bind failure / mid-run crash) → log and retry
    ///     after backoff (2s doubling to 30s); the signal stays PENDING while it
    ///     has never been Ready, and remains Ready across re-entry once it has;</item>
    ///   <item>host cancellation → <c>SetCancelled</c> and prompt exit;</item>
    ///   <item>an exception escaping the loop itself → <c>SetFailed(ex)</c>
    ///     (defensive only — with retry-forever the normal path has no
    ///     unrecoverable failure).</item>
    /// </list>
    /// The terminal <c>SetCancelled</c> after the try/catch is a structural
    /// fallback: a no-op once a state has been set (TrySet semantics), it
    /// guarantees the signal is resolved even on an exit the catches missed.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Task.Yield cannot throw on a cancelled token (unlike Task.Delay),
            // so a pre-cancelled stoppingToken still reaches the branches below
            // that resolve the signal — it is never skipped over.
            await Task.Yield();

            if (!_needsRouter)
            {
                // No real PLC targets configured — the embedded router is not needed
                // (simulated targets talk to an in-memory store, not AMS/ADS).
                // Signal ready immediately so the pool can proceed without delay.
                _logger.LogInformation(
                    "No real PLC targets configured — embedded router not started");
                _readySignal.SetReady();
                return;
            }

            if (string.IsNullOrEmpty(_netId))
            {
                _logger.LogInformation("Embedded ADS router disabled — using system router");

                // Configured routes have nowhere to go: there is no embedded router
                // and the system router owns its own route table. Saying so is the
                // point — a route silently ignored is the failure this section was
                // added to remove.
                if (_routes.Count > 0)
                {
                    _logger.LogWarning(
                        "Ignoring {Count} configured AmsRouter:Routes entr{Suffix} — the embedded " +
                        "router is disabled because AmsRouter:NetId is not set, and the system " +
                        "router keeps its own route table. Set AmsRouter:NetId to use them.",
                        _routes.Count,
                        _routes.Count == 1 ? "y" : "ies");
                }

                _readySignal.SetReady();
                return;
            }

            // Honour a pre-cancelled token explicitly: with no real bind work yet,
            // surface cancellation (resolved via SetCancelled in the catch below)
            // instead of entering the retry loop.
            stoppingToken.ThrowIfCancellationRequested();

            await RunRetryLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting the router down: resolve the signal as Cancelled
            // (distinct from Failed). With the non-blocking pool this only stops
            // any still-deferred real-target loops from starting.
            _logger.LogInformation("Embedded ADS router stopped");
            _readySignal.SetCancelled();
        }
        catch (Exception ex)
        {
            // Defensive only: with retry-forever, RunRetryLoopAsync handles every
            // attempt failure internally, so an exception reaching here is a
            // genuinely unexpected escape. Capture the reason so awaiters (and the
            // pool's log) can report why the router is unavailable.
            _logger.LogError(ex, "Embedded ADS router failed");
            _readySignal.SetFailed(ex);
        }

        // Structural guarantee that the signal is resolved on EVERY exit. This is
        // a no-op whenever a state was already set above (TrySet semantics); it
        // only matters for an exit none of the catches handled.
        _readySignal.SetCancelled();
    }

    /// <summary>
    /// Runs the per-attempt router body inside a backoff retry loop until the
    /// stopping token fires. Each failed attempt is logged and retried after a
    /// delay that starts at 2 seconds and doubles up to a 30-second cap; the
    /// backoff resets to its minimum after an attempt completes (mirroring the
    /// connection pool's loops). Cancellation propagates out so
    /// <see cref="ExecuteAsync"/> can resolve the signal as Cancelled.
    /// </summary>
    private async Task RunRetryLoopAsync(CancellationToken stoppingToken)
    {
        var delay = MinRetryDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation(
                    "Starting embedded ADS router with NetId {NetId}", _netId);

                await RunRouterAttemptAsync(_readySignal, stoppingToken).ConfigureAwait(false);

                // The attempt returned without cancellation: the router stopped or
                // crashed mid-run. Reset the backoff and retry to restore the
                // transport. (The signal is already Ready if it had started, so the
                // pool's already-released loops keep their own reconnect behaviour;
                // re-entry here just rebuilds the AMS/ADS transport.)
                delay = MinRetryDelay;
                _logger.LogWarning(
                    "Embedded ADS router stopped unexpectedly — retrying in {Delay}s",
                    MinRetryDelay.TotalSeconds);
            }
            catch (Exception ex) when (ex is OperationCanceledException && stoppingToken.IsCancellationRequested)
            {
                // Shutdown — let it bubble to ExecuteAsync's cancellation catch.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Embedded ADS router failed to start: {Message} — retrying in {Delay}s",
                    ex.Message,
                    delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
        }

        stoppingToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Runs one router start attempt: constructs the <c>AmsTcpIpRouter</c> via
    /// <see cref="CreateRouter"/>, wires the status hook that resolves
    /// <paramref name="signal"/> as Ready on <c>RouterStatus.Started</c>, and
    /// awaits <c>StartAsync</c>. Returning (or throwing) re-enters the retry loop.
    /// </summary>
    /// <remarks>
    /// Extracted as a <c>protected internal virtual</c> seam so the retry loop is
    /// testable without the un-fakeable Beckhoff router: a test subclass overrides
    /// this to script failing-then-succeeding attempts while exercising the real
    /// backoff via <see cref="TimeProvider"/>.
    /// </remarks>
    /// <param name="signal">The readiness signal to set Ready on a successful start.</param>
    /// <param name="ct">The stopping token cancelled on host shutdown.</param>
    protected internal virtual async Task RunRouterAttemptAsync(
        AdsRouterReadySignal signal, CancellationToken ct)
    {
        AmsTcpIpRouter router = CreateRouter();

        // The whole body of the handler lives in HandleRouterStatusChanged so the
        // route-adding step is reachable from a test: AmsTcpIpRouter cannot be faked,
        // and StartAsync does not return until the router stops, so this hook is both
        // the only "after start" moment and the only testable one.
        router.RouterStatusChanged += (_, e) =>
            HandleRouterStatusChanged(e.RouterStatus, signal, router.TryAddRoute);

        try
        {
            await router.StartAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // AmsTcpIpRouter is not IDisposable; Stop() is its only teardown and
            // the AMS TCP listener stays bound until it is called. Without this,
            // a router that crashed mid-run would keep the port bound and every
            // subsequent retry attempt would fail with AddressInUse forever —
            // defeating the recovery this loop exists for.
            try { router.Stop(); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Stopping the ADS router after an attempt ended threw (best effort)");
            }
        }
    }

    /// <summary>
    /// The body of the <c>RouterStatusChanged</c> hook: logs the status and, once the
    /// router reports <c>RouterStatus.Started</c>, adds the configured remote routes
    /// and THEN resolves <paramref name="signal"/> as Ready.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Routes are added here, after the router has started.</b> That ordering is
    /// what was verified against live hardware — a route added after <c>StartAsync</c>
    /// connected, where the same code path without one failed with
    /// <c>TargetMachineNotFound</c>. Before-start was never isolated as a variable, so
    /// this matches what is proven rather than what seems equivalent. It is also the
    /// only moment available: <c>StartAsync</c> does not return until the router
    /// stops.
    /// </para>
    /// <para>
    /// <b>Routes before <c>SetReady</c>, deliberately.</b> The signal is what releases
    /// the connection pool's real-target loops, so resolving it first would let a pool
    /// connection race a route that is not in the table yet — reintroducing the exact
    /// <c>TargetMachineNotFound</c> this exists to prevent, intermittently.
    /// </para>
    /// <para>
    /// Extracted from the lambda as an <see langword="internal"/> seam because
    /// <c>AmsTcpIpRouter</c> cannot be faked: a test drives this with a recording
    /// <paramref name="tryAddRoute"/> and so covers both the loop and the fact that
    /// the started hook calls it.
    /// </para>
    /// </remarks>
    /// <param name="status">The status the router just reported.</param>
    /// <param name="signal">The readiness signal to resolve on a successful start.</param>
    /// <param name="tryAddRoute">
    /// The router's <c>TryAddRoute</c>, returning <see langword="false"/> when the
    /// route was rejected.
    /// </param>
    internal void HandleRouterStatusChanged(
        RouterStatus status,
        AdsRouterReadySignal signal,
        Func<Route, bool> tryAddRoute)
    {
        _logger.LogInformation("ADS-Router Status: {Status}", status);

        if (status != RouterStatus.Started)
            return;

        ApplyConfiguredRoutes(tryAddRoute);
        signal.SetReady();
    }

    /// <summary>
    /// Hands every configured <see cref="AmsRouteOptions"/> entry to the router's
    /// <c>TryAddRoute</c>, logging each at Information and a rejected one at Warning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A rejected route is logged rather than thrown, because throwing here would
    /// re-enter the retry loop and tear down a router that is otherwise working —
    /// making one unreachable device cost every reachable one. It is logged at Warning
    /// naming the entry, since a silently dropped route is the failure mode this
    /// method exists to remove.
    /// </para>
    /// <para>
    /// <b>The Net ID goes to <c>AmsNetId.Parse</c> unchecked, and that is safe by
    /// construction rather than by luck.</b> <c>AmsNetId.Parse</c> LAUNDERS an
    /// out-of-range octet rather than throwing (<c>999.1.1.1.1.1</c> becomes
    /// <c>0.1.1.1.1.1</c>), so an unvalidated value here would add a route pointing at
    /// a device the operator never named. It cannot arrive: every configured route is
    /// held to <c>AmsNetIdRule.Require</c> by
    /// <see cref="TwinCatAdsOptionsValidator"/>, this service is
    /// <see langword="internal"/>, and its single registration carries
    /// <c>ValidateOnStart</c>. A defence-in-depth re-check lived here until 0.6.0; it
    /// was removed when the rule gained one enforcement point, because two responses
    /// to one input class is how the two drift apart.
    /// </para>
    /// </remarks>
    /// <param name="tryAddRoute">The router's <c>TryAddRoute</c>.</param>
    internal void ApplyConfiguredRoutes(Func<Route, bool> tryAddRoute)
    {
        foreach (var configured in _routes)
        {
            var route = new Route(
                configured.Name,
                AmsNetId.Parse(configured.NetId),
                configured.Address);

            if (tryAddRoute(route))
            {
                _logger.LogInformation(
                    "Added ADS route '{Name}' for {NetId} at {Address}",
                    configured.Name,
                    configured.NetId,
                    configured.Address);
            }
            else
            {
                _logger.LogWarning(
                    "The embedded ADS router REJECTED route '{Name}' for {NetId} at {Address}. " +
                    "That target will fail with TargetMachineNotFound; check for a route already " +
                    "registered under the same name or Net ID",
                    configured.Name,
                    configured.NetId,
                    configured.Address);
            }
        }
    }

    /// <summary>
    /// Constructs an <c>AmsTcpIpRouter</c>, re-evaluating the strategy on every
    /// call so a fresh instance is built per retry attempt.
    /// </summary>
    /// <remarks>
    /// Strategy A — config pass-through (IConfiguration present AND
    ///   AmsRouter:NetId set in that configuration): use
    ///   <c>AmsTcpIpRouter(IConfiguration, ILoggerFactory)</c> so Beckhoff-specific
    ///   keys such as AmsRouter:TcpPort are honoured.
    /// Strategy B — typed NetId (all other cases): use
    ///   <c>AmsTcpIpRouter(AmsNetId, ILoggerFactory)</c> — the Net ID from
    ///   options is forwarded directly; all other settings use Beckhoff defaults.
    /// <para>
    /// Remote ROUTES are added by <see cref="ApplyConfiguredRoutes"/> under BOTH
    /// strategies, and never come from the configuration handed to Beckhoff: no key
    /// under <c>AmsRouter</c> reaches its route table — <c>StaticRoutes:0:*</c>,
    /// <c>RemoteConnections:R:*</c>, <c>Router:StaticRoutes:0:*</c> and
    /// <c>Ams:StaticRoutes:0:*</c> were all measured yielding zero routes. Beckhoff's
    /// only file source is a TwinCAT <c>StaticRoutes.xml</c> on disk, absent on a
    /// machine without a TwinCAT installation.
    /// </para>
    /// </remarks>
    private AmsTcpIpRouter CreateRouter() =>
        UseConfigurationPassThrough(_configuration)
            ? new AmsTcpIpRouter(_configuration!, _loggerFactory)
            : new AmsTcpIpRouter(AmsNetId.Parse(_netId!), _loggerFactory);
}
