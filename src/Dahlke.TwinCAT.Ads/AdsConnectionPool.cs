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

        _logger.LogInformation("ADS-Verbindungspool startet, warte auf Router...");
        try
        {
            await _readySignal.WaitAsync(_stoppingCts.Token).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("ADS-Router nicht verfügbar — Verbindungspool läuft ohne Verbindungen");
            return;
        }
        _logger.LogInformation("ADS-Router bereit, verbinde mit {Count} SPS-Ziel(en)", _targets.Count);

        foreach (var (plcId, options) in _targets)
        {
            StartConnectionLoop(plcId, options);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ADS-Verbindungspool wird gestoppt...");

        // Hintergrundschleifen abbrechen
        foreach (var (_, cts) in _reconnectCts)
            cts.Cancel();

        // Auf Beendigung der Hintergrundschleifen warten
        var loopTasks = _loopTasks.Values.ToArray();
        if (loopTasks.Length > 0)
        {
            try { await Task.WhenAll(loopTasks).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
            catch { /* Timeout oder Abbruch — trotzdem aufräumen */ }
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
                catch (Exception ex) { _logger.LogWarning(ex, "Fehler beim Trennen von SPS {PlcId}", plcId); }
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
            _logger.LogWarning("ForceReconnect: SPS {PlcId} nicht konfiguriert", plcId);
            return;
        }

        _logger.LogInformation("ForceReconnect: Erzwinge Neuverbindung zu SPS {PlcId}", plcId);

        // Alte Schleife abbrechen
        if (_reconnectCts.TryRemove(plcId, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        // Neue Schleife starten
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
    /// Verbindungsschleife: verbindet, prueft regelmaessig die Gesundheit,
    /// und baut bei Fehler eine neue Verbindung auf.
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
                    // Neue Verbindung erstellen
                    ads = (AdsConnection)_connectionFactory.Create(plcId, options);
                    _logger.LogInformation("Verbinde mit SPS {PlcId}...", plcId);
                    ads.Connect();
                    _connections[plcId] = ads;
                    delay = MinReconnectDelay;

                    _logger.LogInformation("SPS {PlcId} verbunden, starte Health-Check", plcId);

                    // Symbolbaum loggen wenn in appsettings aktiviert
                    if (_configuration.GetValue("AdsSymbolTreeDump", false))
                        ads.LogSymbolTree();

                    // Health-Check Schleife: prueft ob Verbindung noch lebt
                    while (!cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(HealthCheckInterval, cts.Token).ConfigureAwait(false);

                        if (!await ads.IsAliveAsync(cts.Token).ConfigureAwait(false))
                        {
                            _logger.LogWarning("SPS {PlcId}: Health-Check fehlgeschlagen, verbinde neu...", plcId);
                            break; // Innere Schleife verlassen -> neu verbinden
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
                    _logger.LogWarning("SPS {PlcId}: Verbindungsfehler: {Message}, erneuter Versuch in {Delay}s",
                        plcId, ex.Message, delay.TotalSeconds);
                }

                // Alte Verbindung aufraeumen: Compare-and-swap entfernt nur die
                // tatsächlich tote Verbindung, nicht eine bereits ersetzte neue
                if (ads is not null)
                {
                    _connections.TryRemove(new KeyValuePair<string, IAdsConnection>(plcId, ads));

                    // Karenzzeit: laufende Operationen auf der alten Verbindung abschliessen lassen
                    try { await Task.Delay(DisposeGracePeriod, cts.Token).ConfigureAwait(false); }
                    catch { /* Bei Abbruch trotzdem aufräumen */ }

                    ads.ForceDisconnect();
                    ads.Dispose();
                }

                if (cts.Token.IsCancellationRequested) break;

                // Warten vor erneutem Verbindungsversuch
                try { await Task.Delay(delay, cts.Token).ConfigureAwait(false); }
                catch { break; }

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxReconnectDelay.Ticks));
            }
        }, CancellationToken.None);
    }
}
