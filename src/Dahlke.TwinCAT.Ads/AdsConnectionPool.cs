using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads;

public sealed class AdsConnectionPool : IHostedService, IAdsConnectionPool, IDisposable
{
    private readonly Dictionary<string, PlcTargetOptions> _targets;
    private readonly IAdsConnectionFactory _connectionFactory;
    private readonly AdsRouterReadySignal _readySignal;
    private readonly ILogger<AdsConnectionPool> _logger;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, IAdsConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _reconnectCts = new();
    private readonly ConcurrentDictionary<string, Task> _loopTasks = new();
    private CancellationTokenSource? _stoppingCts;

    private static readonly TimeSpan MinReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposeGracePeriod = TimeSpan.FromSeconds(2);

    public AdsConnectionPool(
        IOptions<Dictionary<string, PlcTargetOptions>> targets,
        IAdsConnectionFactory connectionFactory,
        AdsRouterReadySignal readySignal,
        ILogger<AdsConnectionPool> logger,
        IConfiguration configuration)
    {
        _targets = targets.Value;
        _connectionFactory = connectionFactory;
        _readySignal = readySignal;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _logger.LogInformation("ADS connection pool starting, waiting for router...");
        try
        {
            await _readySignal.WaitAsync(_stoppingCts.Token).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("ADS router not available — connection pool running without connections");
            return;
        }
        _logger.LogInformation("ADS router ready, connecting to {Count} PLC target(s)", _targets.Count);

        foreach (var (plcId, options) in _targets)
        {
            StartConnectionLoop(plcId, options);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ADS connection pool stopping...");

        // Cancel background loops
        foreach (var (_, cts) in _reconnectCts)
            cts.Cancel();

        // Wait for background loops to finish
        var loopTasks = _loopTasks.Values.ToArray();
        if (loopTasks.Length > 0)
        {
            try { await Task.WhenAll(loopTasks).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
            catch { /* Timeout or cancellation — clean up anyway */ }
        }

        foreach (var (_, cts) in _reconnectCts)
            cts.Dispose();
        _reconnectCts.Clear();
        _loopTasks.Clear();

        foreach (var (plcId, connection) in _connections)
        {
            if (connection is AdsConnection ads)
            {
                try { ads.Disconnect(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error disconnecting from PLC {PlcId}", plcId); }
            }
            if (connection is IDisposable d) d.Dispose();
        }

        _connections.Clear();
        _stoppingCts?.Cancel();
        _stoppingCts?.Dispose();
        _stoppingCts = null;
    }

    public IAdsConnection? GetConnection(string plcId)
    {
        _connections.TryGetValue(plcId, out var connection);
        return connection;
    }

    public IReadOnlyDictionary<string, IAdsConnection> GetAllConnections()
    {
        return _connections.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public void ForceReconnect(string plcId)
    {
        if (!_targets.TryGetValue(plcId, out var options))
        {
            _logger.LogWarning("ForceReconnect: PLC {PlcId} not configured", plcId);
            return;
        }

        _logger.LogInformation("ForceReconnect: forcing reconnection to PLC {PlcId}", plcId);

        // Cancel old loop
        if (_reconnectCts.TryRemove(plcId, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        // Start new loop
        StartConnectionLoop(plcId, options);
    }

    public void Dispose()
    {
        foreach (var (_, cts) in _reconnectCts)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
        foreach (var (_, connection) in _connections)
        {
            if (connection is IDisposable disposable)
                disposable.Dispose();
        }
        _stoppingCts?.Dispose();
    }

    /// <summary>
    /// Connection loop: connects, periodically checks health,
    /// and rebuilds connection on failure.
    /// </summary>
    private void StartConnectionLoop(string plcId, PlcTargetOptions options)
    {
        var cts = new CancellationTokenSource();
        _reconnectCts[plcId] = cts;

        _loopTasks[plcId] = Task.Run(async () =>
        {
            var delay = MinReconnectDelay;

            while (!cts.Token.IsCancellationRequested)
            {
                AdsConnection? ads = null;
                try
                {
                    // Create new connection
                    ads = (AdsConnection)_connectionFactory.Create(plcId, options);
                    _logger.LogInformation("Connecting to PLC {PlcId}...", plcId);
                    ads.Connect();
                    _connections[plcId] = ads;
                    delay = MinReconnectDelay;

                    _logger.LogInformation("PLC {PlcId} connected, starting health check", plcId);

                    // Log symbol tree if enabled in appsettings
                    if (_configuration.GetValue("AdsSymbolTreeDump", false))
                        ads.LogSymbolTree();

                    // Health check loop: checks if connection is still alive
                    while (!cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(HealthCheckInterval, cts.Token).ConfigureAwait(false);

                        if (!await ads.IsAliveAsync(cts.Token).ConfigureAwait(false))
                        {
                            _logger.LogWarning("PLC {PlcId}: health check failed, reconnecting...", plcId);
                            break; // Exit inner loop -> reconnect
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (cts.Token.IsCancellationRequested) break;
                    _logger.LogWarning("PLC {PlcId}: connection error: {Message}, retrying in {Delay}s",
                        plcId, ex.Message, delay.TotalSeconds);
                }

                // Clean up old connection: compare-and-swap removes only the
                // actually dead connection, not an already replaced new one
                if (ads is not null)
                {
                    _connections.TryRemove(new KeyValuePair<string, IAdsConnection>(plcId, ads));

                    // Grace period: let in-flight operations on old connection finish
                    try { await Task.Delay(DisposeGracePeriod, cts.Token).ConfigureAwait(false); }
                    catch { /* Clean up anyway on cancellation */ }

                    ads.ForceDisconnect();
                    ads.Dispose();
                }

                if (cts.Token.IsCancellationRequested) break;

                // Wait before next connection attempt
                try { await Task.Delay(delay, cts.Token).ConfigureAwait(false); }
                catch { break; }

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxReconnectDelay.Ticks));
            }
        }, CancellationToken.None);
    }
}
