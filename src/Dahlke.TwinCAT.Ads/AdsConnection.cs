using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.SumCommand;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// A connected ADS session wrapping a Beckhoff <see cref="AdsClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread safety.</b> All members are safe for concurrent use from any thread; operations on a
/// single connection may interleave freely. No operation blocks another. This is safe because
/// Beckhoff <see cref="AdsClient"/> assigns every outgoing request a unique numeric invoke-id and
/// correlates the response to the pending <see cref="System.Threading.Tasks.TaskCompletionSource{T}"/>
/// via an internal <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// keyed by that id (field <c>_invokeIdDict</c> in <c>AdsClientServer</c>, confirmed by XML
/// documentation in <c>TwinCAT.Ads.xml</c> and by <c>ConcurrentDictionary</c> strings in the
/// shipped binary). The ADS protocol itself multiplexes requests over a single transport channel
/// using those invoke-ids; each async call gets its own id and its own completion task regardless
/// of how many other calls are in-flight.
/// </para>
/// <para>
/// <b>Subscription callbacks.</b> Callbacks registered via
/// <see cref="SubscribeAsync(string,int,Action{string,object?},CancellationToken)"/> are invoked on
/// a background thread owned by the underlying ADS client — never the caller's thread. Callbacks
/// must be thread-safe and must not block; an exception thrown by a callback is caught, logged at
/// Warning severity, and does not interrupt the subscription.
/// </para>
/// </remarks>
internal sealed class AdsConnection : IManagedConnection
{
    private readonly AdsClient _client;
    private readonly PlcTargetOptions _options;
    private readonly ILogger<AdsConnection> _logger;
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

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Same skip-the-read condition as <see cref="ReadValuesAsync"/>.</b> Reuses
    /// <see cref="PlcValueDecoder.DecodesFromSubSymbolsOnly"/> — the exact predicate the batch
    /// container branch uses — to decide whether the top-level <c>_client.ReadValueAsync(symbol,
    /// ct)</c> is needed at all. Structs/function blocks with sub-symbols decode purely from
    /// their own members, so the top-level read is skipped entirely; arrays (which need
    /// <c>Array.Length</c> and element access) and opaque structs/function blocks with no
    /// sub-symbols (which pass their raw value straight through) still need it. Reusing the
    /// shared predicate — rather than re-deriving the condition here — keeps a single struct
    /// read the same shape whether it goes through this method or through a one-symbol
    /// <see cref="ReadValuesAsync"/> batch.
    /// </para>
    /// <para>
    /// <b>Timeout/cancellation.</b> One linked <see cref="CancellationTokenSource"/> bounds the
    /// top-level read (when performed) AND every recursive struct member / array element read
    /// <see cref="PlcValueDecoder"/> performs, exactly as in <see cref="ReadValuesAsync"/>.
    /// Caller cancellation throws <see cref="OperationCanceledException"/>; the per-target
    /// <see cref="PlcTargetOptions.TimeoutMs"/> elapsing throws <see cref="TimeoutException"/> —
    /// both via <see cref="CancellationDisambiguator"/>.
    /// </para>
    /// </remarks>
    public async Task<AdsValueResult> ReadValueWithMetadataAsync(string symbolPath, CancellationToken ct)
    {
        using var cts = CreateTimeoutCts(ct);
        var symbolLoader = GetSymbolLoader();

        if (!symbolLoader.Symbols.TryGetInstance(symbolPath, out var symbol) || symbol is not IValueSymbol)
            throw new AdsErrorException($"Symbol '{symbolPath}' not found.", AdsErrorCode.DeviceSymbolNotFound);

        try
        {
            object? decoded;

            if (PlcValueDecoder.DecodesFromSubSymbolsOnly(symbol))
            {
                // Struct/function-block with sub-symbols: the decoder reads every member itself
                // and never consults the value passed in beyond a null check, so the top-level
                // read is fetched-and-discarded if performed — skip it. See
                // PlcValueDecoder.DecodesFromSubSymbolsOnly's remarks and ReadValuesAsync's
                // container branch, which this mirrors.
                decoded = await PlcValueDecoder.DecodeAsync(SkippedReadPlaceholder, symbol, cts.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                // Arrays need the raw value itself (Array.Length + element access); opaque
                // structs/function blocks with no sub-symbols pass their raw value through
                // unchanged. Both genuinely need this read.
                var read = await _client.ReadValueAsync(symbol, cts.Token).ConfigureAwait(false);
                if (read.Failed)
                    throw new AdsErrorException(
                        $"Read of symbol '{symbolPath}' on PLC '{PlcId}' failed: {read.ErrorCode}",
                        read.ErrorCode);

                decoded = await PlcValueDecoder.DecodeAsync(read.Value, symbol, cts.Token).ConfigureAwait(false);
            }

            return AdsValueResult.Success(decoded, symbolPath, symbol.TypeName, symbol.Category.ToString());
        }
        catch (OperationCanceledException)
        {
            throw CancellationDisambiguator.CreateException(ct, symbolPath, PlcId, _options.TimeoutMs);
        }
    }

    public Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct)
        => WriteValueAsync(symbolPath, (object)value!, ct);

    /// <summary>
    /// Writes <paramref name="value"/> to the PLC symbol identified by <paramref name="symbolPath"/>.
    /// </summary>
    /// <remarks>
    /// Concurrent calls are safe: the underlying <see cref="AdsClient"/> multiplexes requests over
    /// invoke-ids and correlates responses independently. No write lock is held; concurrent writes
    /// to different (or the same) symbols interleave freely at the ADS transport layer.
    /// </remarks>
    public async Task WriteValueAsync(string symbolPath, object value, CancellationToken ct)
    {
        using var cts = CreateTimeoutCts(ct);
        try
        {
            await _client.WriteSymbolAsync(symbolPath, value, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw CancellationDisambiguator.CreateException(ct, symbolPath, PlcId, _options.TimeoutMs);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Partitioned: sum command for scalars, individual decode for containers.</b> Resolved
    /// symbols are split by <see cref="ISymbol.Category"/>. Scalars, strings and enums share a
    /// single ADS sum command (<see cref="SumSymbolRead"/>) — one round-trip for the whole
    /// scalar subset. Structs, function blocks and arrays are read and decoded individually via
    /// <see cref="PlcValueDecoder"/> so their full nested tree survives (a bare sum command would
    /// return only an opaque flat value for these, silently losing fidelity). Per-symbol
    /// granularity is preserved either way: each symbol's outcome is reported independently via
    /// its <see cref="AdsValueResult"/>, which also carries the symbol's
    /// <see cref="AdsValueResult.TypeName"/> and <see cref="AdsValueResult.Category"/>.
    /// </para>
    /// <para>
    /// <b>Symbol resolution.</b> Symbols that cannot be resolved on the PLC are recorded — before
    /// either read path runs — as a per-symbol <see cref="AdsValueResult.Failure(Exception, string?)"/> carrying an
    /// <see cref="AdsErrorException"/> with <see cref="AdsErrorCode.DeviceSymbolNotFound"/>; they
    /// are excluded from both the sum command and the container reads. Duplicate paths are
    /// de-duplicated. Resolution happens exactly once per path — the partition classifies the
    /// already-resolved <see cref="ISymbol"/>, it never re-resolves.
    /// </para>
    /// <para>
    /// <b>Whole-batch timeout/cancellation.</b> The timeout and cancellation apply to the entire
    /// batch — the sum command, any top-level container read, AND every recursive struct
    /// member / array element read <see cref="PlcValueDecoder"/> performs — as a single
    /// operation: the same linked <see cref="System.Threading.CancellationTokenSource"/> token is
    /// threaded through <see cref="PlcValueDecoder.DecodeAsync"/> down to each
    /// <see cref="IValueSymbol.ReadValueAsync(CancellationToken)"/> call, so a struct with many
    /// members cannot run past the configured timeout. Caller cancellation throws
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
        // excluded from both read paths below. This is the ONLY resolution pass — the partition
        // that follows classifies these already-resolved symbols, it never resolves again.
        var resolved = new Dictionary<string, ISymbol>(paths.Length);

        foreach (var path in paths)
        {
            if (symbolLoader.Symbols.TryGetInstance(path, out var symbol) && symbol is IValueSymbol)
            {
                resolved[path] = symbol;
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

        // If nothing to read after filtering, return early — no sum command, no container reads.
        if (resolved.Count == 0)
            return results;

        // Partition: containers need PlcValueDecoder to preserve their nested tree; everything
        // else (scalars, strings, enums) can share one sum command.
        var containers = resolved.Where(kvp => IsContainer(kvp.Value)).ToList();
        var scalars = resolved.Where(kvp => !IsContainer(kvp.Value)).ToList();

        // One timeout/cancellation budget for the whole batch, whether that means one sum
        // command, one container loop, or both.
        using var cts = CreateTimeoutCts(ct);

        // Scalars: one sum-read round-trip for all of them.
        if (scalars.Count > 0)
        {
            var scalarSymbols = new List<ISymbol>(scalars.Count);
            var scalarPaths = new string[scalars.Count];
            for (var i = 0; i < scalars.Count; i++)
            {
                scalarSymbols.Add(scalars[i].Value);
                scalarPaths[i] = scalars[i].Key;
            }

            ResultSumValues sumResult;
            try
            {
                var sumRead = new SumSymbolRead(_client, scalarSymbols);
                sumResult = await sumRead.ReadAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Whole-batch: caller cancellation → OCE; timeout → TimeoutException.
                var ex = CancellationDisambiguator.CreateException(ct, $"batch({scalarSymbols.Count} symbols)", PlcId, _options.TimeoutMs);
                if (ex is OperationCanceledException oce)
                    throw oce;
                throw (TimeoutException)ex;
            }

            (string? TypeName, string? Category) ScalarMetadata(string path) =>
                resolved.TryGetValue(path, out var sym) ? (sym.TypeName, sym.Category.ToString()) : (null, null);

            // Map per-symbol results, carrying each symbol's type metadata along.
            var mapped = SumResultMapper.MapReadResults(
                scalarPaths,
                sumResult.Values ?? Array.Empty<object?>(),
                sumResult.SubErrors ?? Array.Empty<AdsErrorCode>(),
                ScalarMetadata);

            foreach (var kvp in mapped)
                results[kvp.Key] = kvp.Value;
        }

        // Containers: read and decode individually so the full nested tree survives.
        foreach (var (path, symbol) in containers)
        {
            try
            {
                object? decoded;

                if (PlcValueDecoder.DecodesFromSubSymbolsOnly(symbol))
                {
                    // Structs/function blocks with sub-symbols decode purely by reading each
                    // member individually inside PlcValueDecoder — the top-level read this branch
                    // would otherwise perform is fetched and immediately discarded by the
                    // decoder, so it is skipped entirely (one fewer round-trip per such symbol).
                    // DecodeAsync's `value` argument is consulted only for a null guard on this
                    // path, never returned or inspected further, so any non-null placeholder
                    // satisfies it — see PlcValueDecoder.DecodesFromSubSymbolsOnly's remarks.
                    decoded = await PlcValueDecoder.DecodeAsync(SkippedReadPlaceholder, symbol, cts.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    // Arrays need the raw value itself (Array.Length + element access); opaque
                    // structs/function blocks with no sub-symbols pass their raw value through
                    // unchanged. Both genuinely need this read.
                    var read = await _client.ReadValueAsync(symbol, cts.Token).ConfigureAwait(false);
                    if (read.Failed)
                    {
                        results[path] = AdsValueResult.Failure(
                            new AdsErrorException(
                                $"Read of symbol '{path}' on PLC '{PlcId}' failed: {read.ErrorCode}",
                                read.ErrorCode),
                            path);
                        continue;
                    }

                    decoded = await PlcValueDecoder.DecodeAsync(read.Value, symbol, cts.Token).ConfigureAwait(false);
                }

                results[path] = AdsValueResult.Success(decoded, path, symbol.TypeName, symbol.Category.ToString());
            }
            catch (OperationCanceledException)
            {
                // Whole-batch cancellation/timeout is NOT a per-symbol failure — rethrow.
                var ex = CancellationDisambiguator.CreateException(ct, path, PlcId, _options.TimeoutMs);
                if (ex is OperationCanceledException oce)
                    throw oce;
                throw (TimeoutException)ex;
            }
            catch (Exception ex)
            {
                results[path] = AdsValueResult.Failure(ex, path);
            }
        }

        return results;
    }

    /// <summary>
    /// Passed as the <c>value</c> argument to <see cref="PlcValueDecoder.DecodeAsync"/> when the
    /// top-level read was skipped for a symbol where
    /// <see cref="PlcValueDecoder.DecodesFromSubSymbolsOnly"/> is <see langword="true"/>: the
    /// decoder only ever checks this argument for null before switching to reading its own
    /// sub-symbols on that path, so any non-null instance works. Never inspected beyond that
    /// null check.
    /// </summary>
    private static readonly object SkippedReadPlaceholder = new();

    /// <summary>
    /// Classifies a resolved symbol as a container (struct, function block or array) whose value
    /// must be decoded individually via <see cref="PlcValueDecoder"/> to preserve its nested tree,
    /// as opposed to a scalar/string/enum that can share a sum command with other symbols.
    /// Internal (rather than private) so the classification itself — independent of any ADS
    /// round-trip — is directly unit-testable.
    /// </summary>
    internal static bool IsContainer(ISymbol symbol) =>
        symbol.Category is DataTypeCategory.Struct or DataTypeCategory.FunctionBlock or DataTypeCategory.Array;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>One round-trip.</b> All writable symbols are written in a single ADS sum command
    /// (<see cref="SumSymbolWrite"/>) rather than one write per symbol. Per-symbol granularity is
    /// preserved via each symbol's <see cref="AdsValueResult"/>.
    /// </para>
    /// <para>
    /// <b>Pre-filtering.</b> A <see langword="null"/> value is a per-symbol programming error,
    /// recorded as a <see cref="AdsValueResult.Failure(Exception, string?)"/> (an <see cref="ArgumentNullException"/>)
    /// before the sum command and excluded from it. Symbols that cannot be resolved are likewise
    /// recorded as a per-symbol <see cref="AdsErrorException"/> failure with
    /// <see cref="AdsErrorCode.DeviceSymbolNotFound"/> and excluded.
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

        // One sum-write round-trip — no serialization lock needed; AdsClient multiplexes
        // concurrent requests via invoke-ids (see class remarks).
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

        return results;
    }

    public async Task<AdsState> GetAdsStateAsync(CancellationToken ct)
    {
        using var cts = CreateTimeoutCts(ct);
        ResultReadDeviceState result;
        try
        {
            result = await _client.ReadStateAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw CancellationDisambiguator.CreateException(ct, "AdsState", PlcId, _options.TimeoutMs);
        }
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
    public Task<IDisposable> SubscribeAsync<T>(string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct)
        => SubscribeAsync(symbolPath, cycleTimeMs, TypedCallbackAdapter.Wrap(callback, _logger), ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, CancellationToken ct)
        => RunBrowseAsync(() =>
        {
            var loader = GetSymbolLoader();

            ISymbolCollection<ISymbol> symbols;
            if (string.IsNullOrEmpty(parentPath))
            {
                symbols = loader.Symbols;
            }
            else
            {
                if (!loader.Symbols.TryGetInstance(parentPath, out var parent))
                    throw new AdsErrorException($"Symbol '{parentPath}' not found.", AdsErrorCode.DeviceSymbolNotFound);
                symbols = parent.SubSymbols;
            }

            return (IReadOnlyList<AdsSymbolInfo>)symbols
                .Select(s => MapSymbol(s, includeChildren: true))
                .ToList();
        }, parentPath ?? "<root>", ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<AdsSymbolInfo>> SearchSymbolsAsync(string pattern, bool includeChildren, CancellationToken ct)
        => RunBrowseAsync(() =>
        {
            var loader = GetSymbolLoader();

            return (IReadOnlyList<AdsSymbolInfo>)FlattenSymbols(loader.Symbols)
                .Where(s => s.InstancePath.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                .Select(s => MapSymbol(s, includeChildren))
                .ToList();
        }, pattern, ct);

    /// <summary>
    /// Runs a synchronous symbol-browse <paramref name="browse"/> on the thread pool, racing it
    /// against <see cref="PlcTargetOptions.SymbolBrowseTimeoutMs"/> (linked to <paramref name="ct"/>)
    /// so the CALLER stops waiting even though the browse itself cannot be interrupted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Beckhoff symbol upload inside <paramref name="browse"/> is a blocking call with no
    /// cancellable overload. Passing a <see cref="CancellationToken"/> into an overload of
    /// <c>Task.Run</c> only prevents the delegate from starting at all; once the delegate is
    /// actually running on its thread-pool thread, that token has no further effect — cancelling
    /// it does not make <c>Task.Run</c>'s returned task complete early. So this method does not
    /// rely on that: it races the browse against a separate
    /// <see cref="Task.Delay(int, CancellationToken)"/> via <see cref="Task.WhenAny(Task, Task)"/>
    /// instead. Whichever finishes first wins:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     If the browse wins, its result (or exception, e.g. a "symbol not found"
    ///     <see cref="AdsErrorException"/>) is awaited and returned/propagated normally. The
    ///     now-pointless timer is cancelled immediately rather than left running for up to
    ///     <see cref="PlcTargetOptions.SymbolBrowseTimeoutMs"/> after this method has already
    ///     returned.
    ///   </description></item>
    ///   <item><description>
    ///     If the timer wins — because <paramref name="ct"/> was cancelled or
    ///     <see cref="PlcTargetOptions.SymbolBrowseTimeoutMs"/> elapsed — this method throws
    ///     immediately via <see cref="CancellationDisambiguator"/> and the browse is ABANDONED: it
    ///     keeps running to completion on its thread-pool thread, but its eventual result is
    ///     discarded. The browse itself is never interrupted, only the caller's wait for it. An
    ///     abandoned browse's eventual FAULT (a real possibility: a browse slow enough to blow
    ///     the budget will often go on to fail — ADS upload error, disconnect mid-upload,
    ///     <see cref="GetSymbolLoader"/> faulting) is still observed and logged at Warning via
    ///     <see cref="LogIfAbandonedBrowseFails"/>, rather than left to surface only as an
    ///     unobserved task exception at finalization.
    ///   </description></item>
    /// </list>
    /// </remarks>
    private async Task<IReadOnlyList<AdsSymbolInfo>> RunBrowseAsync(
        Func<IReadOnlyList<AdsSymbolInfo>> browse, string context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var browseTask = Task.Run(browse);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeoutTask = Task.Delay(_options.SymbolBrowseTimeoutMs, timeoutCts.Token);

        var winner = await Task.WhenAny(browseTask, timeoutTask).ConfigureAwait(false);

        if (ReferenceEquals(winner, browseTask))
        {
            // The timer is now pointless — cancel it so it doesn't sit alive for up to
            // SymbolBrowseTimeoutMs (30s by default) after this method has already returned.
            timeoutCts.Cancel();
            return await browseTask.ConfigureAwait(false);
        }

        // Abandoned: attach a fire-and-forget continuation so a later fault is observed (never
        // an unobserved task exception) and logged — the caller only ever sees the
        // TimeoutException/OperationCanceledException thrown below, never the browse's own
        // exception, so this is the only place that failure can be diagnosed.
        LogIfAbandonedBrowseFails(browseTask, context);

        throw CancellationDisambiguator.CreateException(ct, context, PlcId, _options.SymbolBrowseTimeoutMs);
    }

    /// <summary>
    /// Attaches a fire-and-forget continuation that logs at Warning if the already-abandoned
    /// <paramref name="browseTask"/> later completes with a fault. Accessing
    /// <see cref="Task.Exception"/> inside the continuation marks it observed, so the fault never
    /// surfaces as a <see cref="TaskScheduler.UnobservedTaskException"/> at finalization — which,
    /// on a host configured with <c>ThrowUnobservedTaskExceptions</c>, would crash the process for
    /// something this library cannot control.
    /// </summary>
    private void LogIfAbandonedBrowseFails(Task<IReadOnlyList<AdsSymbolInfo>> browseTask, string context)
    {
        _ = browseTask.ContinueWith(
            t => _logger.LogWarning(
                t.Exception?.GetBaseException(),
                "Abandoned symbol browse for '{Context}' on PLC '{PlcId}' failed after the caller had already stopped waiting for it.",
                context, PlcId),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Maps a Beckhoff <see cref="ISymbol"/> to the neutral <see cref="AdsSymbolInfo"/> shape,
    /// recursing into <see cref="ISymbol.SubSymbols"/> only when <paramref name="includeChildren"/>
    /// is <see langword="true"/> and there are any — otherwise <see cref="AdsSymbolInfo.Children"/>
    /// is <see langword="null"/>, never an empty list.
    /// </summary>
    private static AdsSymbolInfo MapSymbol(ISymbol symbol, bool includeChildren)
    {
        List<AdsSymbolInfo>? children = null;
        if (includeChildren && symbol.SubSymbols.Count > 0)
            children = symbol.SubSymbols.Select(s => MapSymbol(s, includeChildren: true)).ToList();

        return new AdsSymbolInfo(
            symbol.InstancePath,
            symbol.TypeName,
            symbol.Category.ToString(),
            symbol.ByteSize,
            string.IsNullOrEmpty(symbol.Comment) ? null : symbol.Comment,
            children);
    }

    /// <summary>Depth-first walk of the entire symbol tree, used by <see cref="SearchSymbolsAsync"/>.</summary>
    private static IEnumerable<ISymbol> FlattenSymbols(ISymbolCollection<ISymbol> symbols)
    {
        foreach (var symbol in symbols)
        {
            yield return symbol;
            foreach (var child in FlattenSymbols(symbol.SubSymbols))
                yield return child;
        }
    }

    /// <summary>
    /// Logs the PLC symbol tree for diagnostics.
    /// Symbols are included when their depth (dot-count in the symbol's <c>InstancePath</c>)
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

    /// <summary>
    /// Test-only seam: overrides the lazily-created symbol loader with a caller-supplied one,
    /// bypassing <see cref="GetSymbolLoader"/>'s <see cref="SymbolLoaderFactory.Create"/> call
    /// (which requires a live, connected <see cref="AdsClient"/>). Internal — reachable only from
    /// <c>Dahlke.TwinCAT.Ads.Tests</c> via <c>InternalsVisibleTo</c>. Production code never calls
    /// this; it exists solely so batch-read partition tests can inject a fake
    /// <see cref="IDynamicSymbolLoader"/> and exercise <see cref="ReadValuesAsync"/>'s container
    /// branch without hardware.
    /// </summary>
    internal void SetSymbolLoaderForTesting(IDynamicSymbolLoader loader) => _symbolLoader = loader;

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
