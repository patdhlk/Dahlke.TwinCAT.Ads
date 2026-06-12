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
        _netId = options.Value.Router.NetId;
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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1, stoppingToken);

        if (string.IsNullOrEmpty(_netId))
        {
            _logger.LogInformation("Embedded ADS router disabled — using system router");
            _readySignal.SetReady();
            return;
        }

        _logger.LogInformation("Starting embedded ADS router with NetId {NetId}", _netId);

        try
        {
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
            _logger.LogInformation("Embedded ADS router stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embedded ADS router failed");
            _readySignal.SetFailed();
        }
    }
}
