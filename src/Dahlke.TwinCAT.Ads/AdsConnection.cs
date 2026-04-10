using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace Dahlke.TwinCAT.Ads;

public sealed class AdsConnection : IAdsConnection, IDisposable
{
    private readonly AdsClient _client;
    private readonly PlcTargetOptions _options;
    private readonly ILogger<AdsConnection> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _symbolLoaderLock = new();
    private volatile IDynamicSymbolLoader? _symbolLoader;

    public string PlcId { get; }
    public string DisplayName => _options.DisplayName;
    public bool IsConnected => _client.IsConnected;

    public AdsConnection(string plcId, PlcTargetOptions options, ILoggerFactory loggerFactory)
    {
        PlcId = plcId;
        _options = options;
        _logger = loggerFactory.CreateLogger<AdsConnection>();
        _client = new AdsClient();
    }

    public void Connect()
    {
        var amsNetId = AmsNetId.Parse(_options.AmsNetId);
        _client.Connect(amsNetId, _options.Port);
        _logger.LogInformation("Verbunden mit SPS {PlcId} an {AmsNetId}:{Port}", PlcId, _options.AmsNetId, _options.Port);
    }

    public void Disconnect()
    {
        lock (_symbolLoaderLock) { _symbolLoader = null; }
        if (_client.IsConnected)
        {
            _client.Disconnect();
            _logger.LogInformation("Getrennt von SPS {PlcId}", PlcId);
        }
    }

    public void ForceDisconnect()
    {
        lock (_symbolLoaderLock) { _symbolLoader = null; }
        try { _client.Disconnect(); } catch { /* best effort */ }
    }

    public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct)
    {
        using var cts = CreateTimeoutCts(ct);
        var symbolLoader = GetSymbolLoader();

        if (symbolLoader.Symbols.TryGetInstance(symbolPath, out var symbol) && symbol is IValueSymbol valueSymbol)
        {
            object? value = valueSymbol.ReadValue();
            return Task.FromResult<object?>(value);
        }

        throw new AdsErrorException($"Symbol '{symbolPath}' nicht gefunden.", AdsErrorCode.DeviceSymbolNotFound);
    }

    public async Task WriteValueAsync(string symbolPath, object value, CancellationToken ct)
    {
        using var cts = CreateTimeoutCts(ct);
        await _writeLock.WaitAsync(cts.Token).ConfigureAwait(false);
        try
        {
            await _client.WriteSymbolAsync(symbolPath, value, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<Dictionary<string, object?>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct)
    {
        var results = new Dictionary<string, object?>();
        foreach (var path in symbolPaths)
        {
            results[path] = await ReadValueAsync(path, ct).ConfigureAwait(false);
        }
        return results;
    }

    public async Task WriteValuesAsync(Dictionary<string, object> values, CancellationToken ct)
    {
        using var cts = CreateTimeoutCts(ct);
        await _writeLock.WaitAsync(cts.Token).ConfigureAwait(false);
        try
        {
            foreach (var (path, value) in values)
            {
                await _client.WriteSymbolAsync(path, value, cts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<AdsState> GetAdsStateAsync(CancellationToken ct)
    {
        using var cts = CreateTimeoutCts(ct);
        var result = await _client.ReadStateAsync(cts.Token).ConfigureAwait(false);
        return result.State.AdsState;
    }

    /// <summary>
    /// Prüft ob die Verbindung tatsächlich funktioniert (nicht nur IsConnected).
    /// Gibt false zurück wenn ReadState fehlschlägt.
    /// </summary>
    public async Task<bool> IsAliveAsync(CancellationToken ct)
    {
        if (!_client.IsConnected) return false;
        try
        {
            using var cts = CreateTimeoutCts(ct);
            await _client.ReadStateAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct)
    {
        using var cts = CreateTimeoutCts(ct);
        var symbol = _client.ReadSymbol(symbolPath);
        var settings = new NotificationSettings(AdsTransMode.OnChange, cycleTimeMs, 0);
        var notificationHandle = await _client.AddDeviceNotificationAsync(
            symbolPath, symbol.ByteSize, settings, null, cts.Token).ConfigureAwait(false);

        var handler = new EventHandler<AdsNotificationEventArgs>((_, e) =>
        {
            if (e.Handle != notificationHandle.Handle) return;
            try
            {
                var loader = GetSymbolLoader();
                object? value = null;
                if (loader.Symbols.TryGetInstance(symbolPath, out var sym) && sym is IValueSymbol vs)
                    value = vs.ReadValue();
                callback(symbolPath, value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Notification-Fehler für {Symbol}", symbolPath);
            }
        });

        _client.AdsNotification += handler;

        return new NotificationSubscription(() =>
        {
            _client.AdsNotification -= handler;
            try { _client.DeleteDeviceNotification(notificationHandle.Handle); }
            catch (Exception ex) { _logger.LogWarning(ex, "Fehler beim Löschen der Notification für {Symbol}", symbolPath); }
        });
    }

    /// <summary>
    /// Loggt alle PLC-Symbole (flach, rekursiv) für Diagnose nach PLC-Programmupdate.
    /// </summary>
    public void LogSymbolTree()
    {
        try
        {
            var settings = new SymbolLoaderSettings(SymbolsLoadMode.DynamicTree);
            var loader = SymbolLoaderFactory.Create(_client, settings);

            // SymbolIterator mit rekursiver Suche — wie in Beckhoff-Docs empfohlen
            var iterator = new SymbolIterator(loader.Symbols, recurse: true);

            _logger.LogInformation("=== PLC-Symbolbaum ({Count} Top-Level) ===", loader.Symbols.Count);
            foreach (var sym in iterator)
            {
                var depth = sym.InstancePath.Count(c => c == '.');
                // GVL_Visu und PRGMain bis Tiefe 3 loggen (Struct-Member sichtbar)
                var isRelevant = sym.InstancePath.StartsWith("GVL_Visu") ||
                                 sym.InstancePath.StartsWith("PRGMain");
                var maxDepth = isRelevant ? 3 : 1;
                if (depth <= maxDepth)
                {
                    _logger.LogInformation("  {Path} [{Type}, {Size}B]",
                        sym.InstancePath, sym.TypeName, sym.ByteSize);
                }
            }
            _logger.LogInformation("=== Ende Symbolbaum ===");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fehler beim Lesen des Symbolbaums");
        }
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        _client.Dispose();
    }

    private IDynamicSymbolLoader GetSymbolLoader()
    {
        var loader = _symbolLoader;
        if (loader is not null)
            return loader;

        lock (_symbolLoaderLock)
        {
            loader = _symbolLoader;
            if (loader is not null)
                return loader;

            var settings = new SymbolLoaderSettings(SymbolsLoadMode.DynamicTree);
            loader = (IDynamicSymbolLoader)SymbolLoaderFactory.Create(_client, settings);
            _symbolLoader = loader;
            return loader;
        }
    }

    private CancellationTokenSource CreateTimeoutCts(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.TimeoutMs);
        return cts;
    }

    private sealed class NotificationSubscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
