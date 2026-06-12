using Microsoft.Extensions.Options;
using TwinCAT.Ads;
using TwinCAT.Ads.TcpRouter;
using TwinCAT.Router;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Hosted service that manages the optional embedded AMS TCP/IP router.
/// When <see cref="AmsRouterOptions.NetId"/> is <see langword="null"/> or empty
/// the service exits immediately and the host is expected to have a system
/// TwinCAT router already running.
/// </summary>
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
public sealed class AdsRouterService : BackgroundService
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AdsRouterService> _logger;
    private readonly AdsRouterReadySignal _readySignal;
    private readonly string? _netId;
    private readonly bool _hasRealTargets;

    // IConfiguration is optional — null when the application does not register
    // it (pure code-first scenario).  When present the full application config is
    // forwarded to AmsTcpIpRouter so Beckhoff-specific keys (AmsRouter:TcpPort,
    // static-routes, etc.) remain honoured.
    private readonly IConfiguration? _configuration;

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
    public AdsRouterService(
        IOptions<TwinCatAdsOptions> options,
        IConfiguration? configuration,
        ILoggerFactory loggerFactory,
        AdsRouterReadySignal readySignal)
    {
        var value = options.Value;
        _netId = value.Router.NetId;
        _hasRealTargets = value.Targets.Values.Any(t => t.Mode == ConnectionMode.Real);
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AdsRouterService>();
        _readySignal = readySignal;
    }

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
    /// Every exit path resolves <see cref="AdsRouterReadySignal"/> exactly once
    /// so the pool can never hang waiting on it:
    /// <list type="bullet">
    ///   <item>no real targets / no NetId / router started → <c>SetReady</c>;</item>
    ///   <item>host cancellation (including a pre-cancelled token) → <c>SetCancelled</c>;</item>
    ///   <item>any other failure → <c>SetFailed(ex)</c> carrying the reason.</item>
    /// </list>
    /// The terminal <c>SetCancelled</c> after the try/catch is a structural
    /// fallback: it is a no-op once a state has been set (TrySet semantics), but
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

            if (!_hasRealTargets)
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
                _readySignal.SetReady();
                return;
            }

            // Honour a pre-cancelled token explicitly: with no real bind work yet,
            // surface cancellation (resolved via SetCancelled in the catch below)
            // instead of starting the router.
            stoppingToken.ThrowIfCancellationRequested();

            _logger.LogInformation("Starting embedded ADS router with NetId {NetId}", _netId);

            // Choose the AmsTcpIpRouter constructor via the pure strategy method.
            //
            // Strategy A — config pass-through (IConfiguration present AND
            //   AmsRouter:NetId is set in that configuration):
            //   Use AmsTcpIpRouter(IConfiguration, ILoggerFactory) so that all
            //   Beckhoff-specific keys in appsettings.json (AmsRouter:TcpPort,
            //   StaticRoutes, etc.) are honoured.
            //
            // Strategy B — typed NetId (all other cases, including Generic Host /
            //   ASP.NET Core where IConfiguration is always present but may NOT
            //   carry AmsRouter:NetId because the developer used the code-first
            //   overload):
            //   Use AmsTcpIpRouter(AmsNetId, ILoggerFactory) — the Net ID from
            //   options is forwarded directly; all other router settings use
            //   Beckhoff defaults.
            AmsTcpIpRouter router = UseConfigurationPassThrough(_configuration)
                ? new AmsTcpIpRouter(_configuration!, _loggerFactory)
                : new AmsTcpIpRouter(AmsNetId.Parse(_netId), _loggerFactory);

            router.RouterStatusChanged += (_, e) =>
            {
                _logger.LogInformation("ADS-Router Status: {Status}", e.RouterStatus);
                if (e.RouterStatus == RouterStatus.Started)
                    _readySignal.SetReady();
            };
            await router.StartAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting the router down before it became ready: resolve
            // the signal as Cancelled (distinct from Failed) so the pool stops
            // waiting and routes into its "router not available" path.
            _logger.LogInformation("Embedded ADS router stopped");
            _readySignal.SetCancelled();
        }
        catch (Exception ex)
        {
            // Genuine failure: capture the reason so awaiters (and the pool's
            // log) can report why the router is unavailable.
            _logger.LogError(ex, "Embedded ADS router failed");
            _readySignal.SetFailed(ex);
        }

        // Structural guarantee that the signal is resolved on EVERY exit. This is
        // a no-op whenever a state was already set above (TrySet semantics); it
        // only matters for an exit none of the catches handled — e.g. router
        // StartAsync returning without ever raising RouterStatus.Started.
        _readySignal.SetCancelled();
    }
}
