using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace Dahlke.TwinCAT.Ads;

public sealed class AdsConnection : IManagedConnection
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

    /// <inheritdoc />
    /// <remarks>
    /// Derived from <see cref="IsConnected"/>: returns
    /// <see cref="ConnectionState.Connected"/> when the underlying ADS client is
    /// connected, and <see cref="ConnectionState.Disconnected"/> otherwise.
    /// Pool-driven lifecycle transitions (including
    /// <see cref="ConnectionState.Connecting"/>) are surfaced on the
    /// <see cref="AdsConnectionFacade"/> that wraps this instance; consumers do
    /// not hold <see cref="AdsConnection"/> directly.
    /// </remarks>
    public ConnectionState State => _client.IsConnected
        ? ConnectionState.Connected
        : ConnectionState.Disconnected;

    /// <inheritdoc />
    /// <remarks>
    /// Pool-driven transitions are surfaced on the wrapping
    /// <see cref="AdsConnectionFacade"/>; this event is never raised on the raw
    /// <see cref="AdsConnection"/> instance.
    /// </remarks>
#pragma warning disable CS0067 // The event is never used — by design; see remarks.
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
#pragma warning restore CS0067

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
        _logger.LogInformation("Connected to PLC {PlcId} at {AmsNetId}:{Port}", PlcId, _options.AmsNetId, _options.Port);
    }

    public void Disconnect()
    {
        lock (_symbolLoaderLock) { _symbolLoader = null; }
        if (_client.IsConnected)
        {
            _client.Disconnect();
            _logger.LogInformation("Disconnected from PLC {PlcId}", PlcId);
        }
    }

    public void ForceDisconnect()
    {
        lock (_symbolLoaderLock) { _symbolLoader = null; }
        try { _client.Disconnect(); } catch { /* best effort */ }
    }

    public async Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct)
    {
        // NOTE: making this a proper async method (not a sync throw + Task.FromResult) is itself a
        // subtle behavioral fix: any synchronous exception (symbol not found) now arrives via the
        // Task rather than being thrown before the task is returned. The facade awaits this method
        // so consumers see no difference in how exceptions surface, but it is safer API practice.

        using var cts = CreateTimeoutCts(ct);
        var symbolLoader = GetSymbolLoader();

        if (!symbolLoader.Symbols.TryGetInstance(symbolPath, out var symbol) || symbol is not IValueSymbol)
            throw new AdsErrorException($"Symbol '{symbolPath}' not found.", AdsErrorCode.DeviceSymbolNotFound);

        ResultAnyValue result;
        try
        {
            result = await _client.ReadValueAsync(symbol, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Either the caller's token or the timeout CTS fired.
            // Disambiguate: OCE when caller cancelled, TimeoutException when timeout elapsed.
            throw CancellationDisambiguator.CreateException(ct, symbolPath, PlcId, _options.TimeoutMs);
        }

        if (result.Failed)
            throw new AdsErrorException(
                $"Read of symbol '{symbolPath}' on PLC '{PlcId}' failed: {result.ErrorCode}",
                result.ErrorCode);

        return result.Value;
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
    /// Checks whether the connection is actually functional (not just IsConnected).
    /// Returns false if ReadState fails.
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
                _logger.LogWarning(ex, "Notification error for {Symbol}", symbolPath);
            }
        });

        _client.AdsNotification += handler;

        return new NotificationSubscription(() =>
        {
            _client.AdsNotification -= handler;
            try { _client.DeleteDeviceNotification(notificationHandle.Handle); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error deleting notification for {Symbol}", symbolPath); }
        });
    }

    /// <summary>
    /// Logs the PLC symbol tree for diagnostics.
    /// Symbols are included when their depth (dot-count in <see cref="ISymbol.InstancePath"/>)
    /// is at most <see cref="SymbolDumpOptions.MaxDepth"/> and, when
    /// <see cref="SymbolDumpOptions.Prefixes"/> is non-empty, the path starts with
    /// at least one configured prefix (case-insensitive).
    /// Filter logic is delegated to <see cref="SymbolDumpFilter.ShouldInclude"/>.
    /// </summary>
    public void LogSymbolTree(SymbolDumpOptions options)
    {
        try
        {
            var settings = new SymbolLoaderSettings(SymbolsLoadMode.DynamicTree);
            var loader = SymbolLoaderFactory.Create(_client, settings);

            // SymbolIterator with recursive search — as recommended in Beckhoff docs.
            var iterator = new SymbolIterator(loader.Symbols, recurse: true);

            _logger.LogInformation("=== PLC symbol tree ({Count} top-level) ===", loader.Symbols.Count);
            foreach (var sym in iterator)
            {
                if (SymbolDumpFilter.ShouldInclude(sym.InstancePath, options))
                {
                    _logger.LogInformation("  {Path} [{Type}, {Size}B]",
                        sym.InstancePath, sym.TypeName, sym.ByteSize);
                }
            }
            _logger.LogInformation("=== End symbol tree ===");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading symbol tree");
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
