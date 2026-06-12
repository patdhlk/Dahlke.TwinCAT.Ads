using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.SumCommand;
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
    /// <para>
    /// <b>One round-trip.</b> All resolvable symbols are read in a single ADS sum command
    /// (<see cref="SumSymbolRead"/>) rather than one read per symbol. Per-symbol granularity is
    /// preserved: each symbol's outcome is reported independently via its
    /// <see cref="AdsValueResult"/>.
    /// </para>
    /// <para>
    /// <b>Symbol resolution.</b> Symbols that cannot be resolved on the PLC are recorded — before
    /// the sum command is issued — as a per-symbol <see cref="AdsValueResult.Failure"/> carrying an
    /// <see cref="AdsErrorException"/> with <see cref="AdsErrorCode.DeviceSymbolNotFound"/>; they
    /// are excluded from the sum command. Duplicate paths are de-duplicated.
    /// </para>
    /// <para>
    /// <b>Whole-batch timeout/cancellation.</b> The timeout and cancellation apply to the entire
    /// batch as a single operation. Caller cancellation throws
    /// <see cref="OperationCanceledException"/>; the per-target
    /// <see cref="PlcTargetOptions.TimeoutMs"/> elapsing throws a <see cref="TimeoutException"/> for
    /// the whole batch — neither is recorded as a per-symbol failure.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, AdsValueResult>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct)
    {
        // De-dup: a repeated path is read once.
        var paths = symbolPaths.Distinct().ToArray();

        // Empty input shortcut — no ADS call.
        if (paths.Length == 0)
            return new Dictionary<string, AdsValueResult>();

        ct.ThrowIfCancellationRequested();

        var results = new Dictionary<string, AdsValueResult>();
        var symbolLoader = GetSymbolLoader();

        // Resolve symbols; unresolvable ones become per-symbol failures immediately and are
        // excluded from the sum command.
        var foundSymbols = new List<ISymbol>(paths.Length);
        var foundPaths = new List<string>(paths.Length);

        foreach (var path in paths)
        {
            if (symbolLoader.Symbols.TryGetInstance(path, out var symbol) && symbol is IValueSymbol)
            {
                foundSymbols.Add(symbol);
                foundPaths.Add(path);
            }
            else
            {
                results[path] = AdsValueResult.Failure(
                    new AdsErrorException(
                        $"Symbol '{path}' not found on PLC '{PlcId}'.",
                        AdsErrorCode.DeviceSymbolNotFound),
                    path);
            }
        }

        // If nothing to read after filtering, return early — no sum command.
        if (foundSymbols.Count == 0)
            return results;

        // One sum-read round-trip for all found symbols.
        using var cts = CreateTimeoutCts(ct);
        ResultSumValues sumResult;
        try
        {
            var sumRead = new SumSymbolRead(_client, foundSymbols);
            sumResult = await sumRead.ReadAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Whole-batch: caller cancellation → OCE; timeout → TimeoutException.
            var ex = CancellationDisambiguator.CreateException(ct, $"batch({foundSymbols.Count} symbols)", PlcId, _options.TimeoutMs);
            if (ex is OperationCanceledException oce)
                throw oce;
            throw (TimeoutException)ex;
        }

        // Map per-symbol results.
        var mapped = SumResultMapper.MapReadResults(
            [.. foundPaths],
            sumResult.Values ?? Array.Empty<object?>(),
            sumResult.SubErrors ?? Array.Empty<AdsErrorCode>());

        foreach (var kvp in mapped)
            results[kvp.Key] = kvp.Value;

        return results;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>One round-trip.</b> All writable symbols are written in a single ADS sum command
    /// (<see cref="SumSymbolWrite"/>) rather than one write per symbol. Per-symbol granularity is
    /// preserved via each symbol's <see cref="AdsValueResult"/>.
    /// </para>
    /// <para>
    /// <b>Pre-filtering.</b> A <see langword="null"/> value is a per-symbol programming error,
    /// recorded as a <see cref="AdsValueResult.Failure"/> (an <see cref="ArgumentNullException"/>)
    /// before the sum command and excluded from it. Symbols that cannot be resolved are likewise
    /// recorded as a per-symbol <see cref="AdsErrorException"/> failure with
    /// <see cref="AdsErrorCode.DeviceSymbolNotFound"/> and excluded.
    /// </para>
    /// <para>
    /// <b>Locking.</b> The write lock is held across the single sum write. The lock wait uses
    /// <paramref name="ct"/> only — a contention wait, not a hardware op — so a cancelled wait
    /// aborts the whole batch via <see cref="OperationCanceledException"/>.
    /// </para>
    /// <para>
    /// <b>Whole-batch timeout/cancellation.</b> As with <see cref="ReadValuesAsync"/>, the timeout
    /// and cancellation apply to the entire batch as a single operation: caller cancellation throws
    /// <see cref="OperationCanceledException"/>; the timeout elapsing throws a
    /// <see cref="TimeoutException"/> for the whole batch.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, AdsValueResult>> WriteValuesAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct)
    {
        // Empty input shortcut — no ADS call.
        if (values.Count == 0)
            return new Dictionary<string, AdsValueResult>();

        ct.ThrowIfCancellationRequested();

        var results = new Dictionary<string, AdsValueResult>();
        var symbolLoader = GetSymbolLoader();

        // Pre-filter: null values and unresolvable symbols are per-symbol failures, excluded from
        // the sum command. Found symbols and their values stay index-aligned.
        var foundSymbols = new List<ISymbol>(values.Count);
        var foundPaths = new List<string>(values.Count);
        var foundValues = new List<object>(values.Count);

        foreach (var (path, value) in values)
        {
            if (value is null)
            {
                results[path] = AdsValueResult.Failure(
                    new ArgumentNullException(
                        $"values[\"{path}\"]",
                        $"Cannot write a null value to symbol '{path}'."),
                    path);
                continue;
            }

            if (symbolLoader.Symbols.TryGetInstance(path, out var symbol) && symbol is IValueSymbol)
            {
                foundSymbols.Add(symbol);
                foundPaths.Add(path);
                foundValues.Add(value);
            }
            else
            {
                results[path] = AdsValueResult.Failure(
                    new AdsErrorException(
                        $"Symbol '{path}' not found on PLC '{PlcId}'.",
                        AdsErrorCode.DeviceSymbolNotFound),
                    path);
            }
        }

        // If nothing to write after filtering, return early — no sum command.
        if (foundSymbols.Count == 0)
            return results;

        // One sum-write round-trip; hold _writeLock for the single operation.
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cts = CreateTimeoutCts(ct);
            ResultSumCommand sumResult;
            try
            {
                var sumWrite = new SumSymbolWrite(_client, foundSymbols);
                sumResult = await sumWrite.WriteAsync([.. foundValues], cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Whole-batch: caller cancellation → OCE; timeout → TimeoutException.
                var ex = CancellationDisambiguator.CreateException(ct, $"batch({foundSymbols.Count} symbols)", PlcId, _options.TimeoutMs);
                if (ex is OperationCanceledException oce)
                    throw oce;
                throw (TimeoutException)ex;
            }

            // Map per-symbol results.
            var mapped = SumResultMapper.MapWriteResults(
                [.. foundPaths],
                sumResult.SubErrors ?? Array.Empty<AdsErrorCode>());

            foreach (var kvp in mapped)
                results[kvp.Key] = kvp.Value;
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
