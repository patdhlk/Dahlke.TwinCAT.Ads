using Microsoft.Extensions.Options;
using TwinCAT.Ads.TcpRouter;
using TwinCAT.Router;

namespace Dahlke.TwinCAT.Ads;

public sealed class AdsRouterService : BackgroundService
{
    // IConfiguration is retained here solely because the Beckhoff
    // AmsTcpIpRouter(IConfiguration, ILoggerFactory) constructor requires it
    // as its documented public API — this is the only remaining raw
    // IConfiguration usage in the internal service layer.
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AdsRouterService> _logger;
    private readonly AdsRouterReadySignal _readySignal;
    private readonly string? _netId;

    public AdsRouterService(
        IOptions<TwinCatAdsOptions> options,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        AdsRouterReadySignal readySignal)
    {
        _netId = options.Value.Router.NetId;
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AdsRouterService>();
        _readySignal = readySignal;
    }

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
            // AmsTcpIpRouter requires IConfiguration per Beckhoff's documented API —
            // see the documented-exception comment on the field declaration above.
            var router = new AmsTcpIpRouter(_configuration, _loggerFactory);
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
