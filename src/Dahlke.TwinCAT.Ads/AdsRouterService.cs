using TwinCAT.Ads.TcpRouter;
using TwinCAT.Router;

namespace Dahlke.TwinCAT.Ads;

public sealed class AdsRouterService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AdsRouterService> _logger;
    private readonly AdsRouterReadySignal _readySignal;

    public AdsRouterService(
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        AdsRouterReadySignal readySignal)
    {
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AdsRouterService>();
        _readySignal = readySignal;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1, stoppingToken);

        var netId = _configuration.GetSection("AmsRouter").GetValue<string>("NetId");

        if (string.IsNullOrEmpty(netId))
        {
            _logger.LogInformation("Embedded ADS router disabled — using system router");
            _readySignal.SetReady();
            return;
        }

        _logger.LogInformation("Starting embedded ADS router with NetId {NetId}", netId);

        try
        {
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
