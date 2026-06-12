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

    public async Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct)
    {
        using var cts = CreateTimeoutCts(ct);

        ResultValue<T> result;
        try
        {
            result = await _client.ReadValueAsync<T>(symbolPath, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw CancellationDisambiguator.CreateException(ct, symbolPath, PlcId, _options.TimeoutMs);
        }

        if (result.Failed)
            throw new AdsErrorException(
                $"Read of symbol '{symbolPath}' on PLC '{PlcId}' failed: {result.ErrorCode}",
                result.ErrorCode);

        // result.Value is non-null when result.Succeeded (we threw above on failure).
        // The Beckhoff annotation is T? for the nullable-oblivious case; suppress the warning.
        return result.Value!;
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

    public Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct)
        => WriteValueAsync(symbolPath, (object)value!, ct);

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

    /// <inheritdoc />
    /// <remarks>
    /// Interim implementation: iterates and issues one single read per distinct symbol, so each
    /// symbol burns its own <see cref="PlcTargetOptions.TimeoutMs"/> window. A later commit adds
    /// sum commands. A per-symbol failure is captured as <see cref="AdsValueResult.Failure"/>;
    /// cancellation rethrows and aborts the whole batch.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, AdsValueResult>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct)
    {
        var results = new Dictionary<string, AdsValueResult>();
        foreach (var path in symbolPaths)
        {
            // De-dup: a repeated path is read once.
            if (results.ContainsKey(path))
                continue;

            try
            {
                var value = await ReadValueAsync(path, ct).ConfigureAwait(false);
                results[path] = AdsValueResult.Success(value, path);
            }
            catch (OperationCanceledException)
            {
                // Cancellation aborts the WHOLE batch, not just this symbol.
                throw;
            }
            catch (Exception ex)
            {
                results[path] = AdsValueResult.Failure(ex, path);
            }
        }
        return results;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Interim implementation: iterates and issues one single write per symbol. A future commit
    /// adds sum commands; the per-symbol result contract is designed to survive that change.
    /// </para>
    /// <para>
    /// <b>Locking.</b> The write lock is acquired ONCE for the whole batch (matching batch write
    /// semantics) using <paramref name="ct"/> only — the lock wait is a contention wait, not a
    /// hardware operation, so it is not subject to a per-symbol timeout. If the lock wait is
    /// cancelled the whole batch is aborted via <see cref="OperationCanceledException"/>.
    /// </para>
    /// <para>
    /// <b>Per-symbol timeout windows.</b> Each individual <c>WriteSymbolAsync</c> call burns its
    /// own <see cref="PlcTargetOptions.TimeoutMs"/> window. A timeout on an individual write is
    /// recorded as a per-symbol <see cref="AdsValueResult.Failure"/> (carrying a
    /// <see cref="TimeoutException"/>) and does NOT abort the batch; only caller cancellation
    /// (via <paramref name="ct"/>) aborts the whole batch.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, AdsValueResult>> WriteValuesAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct)
    {
        var results = new Dictionary<string, AdsValueResult>();

        // Acquire the write lock ONCE for the whole batch. The wait uses ct only: it is a
        // contention wait, not a hardware op, so no per-symbol timeout applies. A cancelled
        // wait aborts the whole batch (OCE propagates).
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var (path, value) in values)
            {
                // A real PLC write needs an actual value; a null is a per-symbol programming
                // error, captured as a failure rather than aborting the batch.
                if (value is null)
                {
                    results[path] = AdsValueResult.Failure(
                        new ArgumentNullException(
                            $"values[\"{path}\"]", $"Cannot write a null value to symbol '{path}'."),
                        path);
                    continue;
                }

                try
                {
                    // Per-symbol timeout window applies only to the write itself.
                    using var cts = CreateTimeoutCts(ct);
                    await _client.WriteSymbolAsync(path, value, cts.Token).ConfigureAwait(false);
                    results[path] = AdsValueResult.Success(null, path);
                }
                catch (OperationCanceledException)
                {
                    // Disambiguate: caller cancellation aborts the batch; a per-symbol timeout
                    // is recorded as a failure and the loop continues.
                    var ex = CancellationDisambiguator.CreateException(ct, path, PlcId, _options.TimeoutMs);
                    if (ex is OperationCanceledException oce)
                        throw oce;
                    results[path] = AdsValueResult.Failure(ex, path);
                }
                catch (Exception ex)
                {
                    results[path] = AdsValueResult.Failure(ex, path);
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }

        return results;
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
    /// Typed subscription: wraps <paramref name="callback"/> with
    /// <see cref="TypedCallbackAdapter.Wrap{T}"/> and delegates to the untyped
    /// <see cref="SubscribeAsync"/>. Each notification value is converted to
    /// <typeparamref name="T"/> with the same rules as
    /// <see cref="ReadValueAsync{T}(string, CancellationToken)"/>; an unconvertible value
    /// is dropped with a Warning rather than delivered.
    /// </summary>
    public Task<IDisposable> SubscribeAsync<T>(string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct = default)
        => SubscribeAsync(symbolPath, cycleTimeMs, TypedCallbackAdapter.Wrap(callback, _logger), ct);

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
