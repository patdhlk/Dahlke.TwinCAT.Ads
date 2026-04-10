namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Simulierter Verbindungspool für Offline-Entwicklung.
/// Erstellt eine einzige SimulatedAdsConnection pro PLC-Ziel.
/// </summary>
public sealed class SimulatedAdsConnectionPool : IHostedService, IAdsConnectionPool, IDisposable
{
    private readonly Dictionary<string, PlcTargetOptions> _targets;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SimulatedAdsConnectionPool> _logger;
    private readonly Dictionary<string, SimulatedAdsConnection> _connections = new(StringComparer.OrdinalIgnoreCase);

    public SimulatedAdsConnectionPool(
        Microsoft.Extensions.Options.IOptions<Dictionary<string, PlcTargetOptions>> targets,
        ILoggerFactory loggerFactory)
    {
        _targets = targets.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SimulatedAdsConnectionPool>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Simulierter ADS-Verbindungspool startet ({Count} SPS-Ziel(e))", _targets.Count);

        foreach (var (plcId, options) in _targets)
        {
            _connections[plcId] = new SimulatedAdsConnection(
                plcId, options.DisplayName, _loggerFactory);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Simulierter ADS-Verbindungspool wird gestoppt");
        foreach (var (_, conn) in _connections)
            conn.Dispose();
        _connections.Clear();
        return Task.CompletedTask;
    }

    public IAdsConnection? GetConnection(string plcId)
    {
        _connections.TryGetValue(plcId, out var connection);
        return connection;
    }

    public IReadOnlyDictionary<string, IAdsConnection> GetAllConnections()
    {
        return _connections.ToDictionary(kvp => kvp.Key, kvp => (IAdsConnection)kvp.Value);
    }

    public void ForceReconnect(string plcId)
    {
        _logger.LogInformation("Simulation: ForceReconnect für {PlcId} ignoriert (immer verbunden)", plcId);
    }

    public void Dispose()
    {
        foreach (var (_, conn) in _connections)
            conn.Dispose();
    }
}
