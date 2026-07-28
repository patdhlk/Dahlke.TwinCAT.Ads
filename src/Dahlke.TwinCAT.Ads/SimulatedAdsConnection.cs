using System.Collections.Concurrent;
using System.Globalization;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// In-memory simulated PLC connection for offline development and testing.
/// Stores values in a thread-safe dictionary — written values are returned on subsequent reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Subscriptions.</b> Callbacks registered via <see cref="SubscribeAsync(string, int, Action{string, object?}, CancellationToken)"/> fire
/// synchronously on the writer's thread, immediately after the value is stored, whenever the
/// written value differs from the previously stored value (<c>!Equals(oldValue, newValue)</c>,
/// using <see cref="object.Equals(object, object)"/>).
/// This is on-change semantics matching <c>AdsTransMode.OnChange</c>, but notification delivery
/// is synchronous and immediate — there is no cycle-time throttle (the <c>cycleTimeMs</c>
/// parameter is accepted for interface compatibility but has no effect on simulation). Real ADS
/// notifies on a background notification thread; simulation notifies on the writer's thread.
/// Callers should design callbacks to be thread-safe regardless.
/// </para>
/// <para>
/// <b>Concurrent writers.</b> Concurrent writes to the same path are resolved by the
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// <c>AddOrUpdate</c> compare-and-swap. Each value change fires callbacks exactly once: the first writer
/// that transitions the stored value from A→B fires the callback; subsequent concurrent writers
/// arriving with the same new value B see the store already at B and do not fire again. This
/// makes the callback-fire-on-change guarantee well-defined under concurrency.
/// </para>
/// <para>
/// <b>Boxed-type equality.</b> Equality uses <see cref="object.Equals(object, object)"/>, which
/// delegates to the runtime type's <c>Equals</c>. A boxed <c>int</c> 42 and a boxed
/// <c>double</c> 42.0 are NOT equal (different types), so writing the same numeric magnitude
/// with different CLR types always counts as a change.
/// </para>
/// <para>
/// <b>Exception safety.</b> A callback that throws is caught, logged at Warning severity, and
/// does not abort the write or prevent subsequent callbacks for the same path from firing.
/// </para>
/// <para>
/// <b>Seeding.</b> <see cref="SetInitialValues"/> writes directly into the store without
/// invoking any callbacks. This is intentional: seeding typically precedes subscriber
/// registration, and firing callbacks during setup would produce spurious initial-value
/// notifications inconsistent with real ADS behaviour (which fires a first notification for
/// each subscriber when it is first registered, not when values are pre-loaded).
/// </para>
/// </remarks>
public sealed class SimulatedAdsConnection : IManagedConnection
{
    private readonly ILogger<SimulatedAdsConnection> _logger;
    private readonly ConcurrentDictionary<string, object?> _symbols = new(StringComparer.OrdinalIgnoreCase);

    // Written by WriteControlAsync, read by GetAdsStateAsync; volatile gives lock-free
    // cross-thread visibility for this simple flag-like field (same rationale as the
    // facade's _stopped/_state fields).
    private volatile AdsState _adsState = AdsState.Run;

    // Per-path subscriber list. Each entry is a list of (unique id → callback) pairs.
    // ConcurrentDictionary provides thread-safe path lookup; the inner lock guards
    // the list under concurrent subscribe/dispose/fire operations.
    private readonly ConcurrentDictionary<string, SubscriberList> _subscribers = new();

    /// <inheritdoc />
    public string PlcId { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    /// <remarks>A simulated connection is permanently connected; this always returns <see langword="true"/>.</remarks>
    public bool IsConnected => true;

    /// <inheritdoc />
    /// <remarks>
    /// A simulated connection is permanently connected; this property always
    /// returns <see cref="ConnectionState.Connected"/>.
    /// </remarks>
    public ConnectionState State => ConnectionState.Connected;

    /// <inheritdoc />
    /// <remarks>
    /// A simulated connection has no lifecycle transitions — it is always
    /// <see cref="ConnectionState.Connected"/> — so this event is never raised.
    /// Subscribing is harmless. When consumers hold the facade returned by
    /// <see cref="IAdsConnectionPool.GetConnection"/> (the normal case) the
    /// facade's own <c>ConnectionStateChanged</c> reports pool-driven transitions
    /// instead; the direct-<c>SimulatedAdsConnection</c> case is mainly for
    /// unit tests and the contract-test adapter.
    /// </remarks>
#pragma warning disable CS0067 // The event is never used — by design; see remarks.
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
#pragma warning restore CS0067

    /// <summary>
    /// Creates an in-memory simulated PLC connection.
    /// </summary>
    /// <param name="plcId">The configured identifier of the simulated target.</param>
    /// <param name="displayName">A human-readable display name for the target.</param>
    /// <param name="loggerFactory">Logger factory used for callback-exception logging.</param>
    public SimulatedAdsConnection(string plcId, string displayName, ILoggerFactory loggerFactory)
    {
        PlcId = plcId;
        DisplayName = displayName;
        _logger = loggerFactory.CreateLogger<SimulatedAdsConnection>();
        _logger.LogInformation("Simulated ADS connection {PlcId} ({DisplayName}) started", plcId, displayName);
    }

    /// <summary>
    /// Pre-populates the simulated symbol store with initial values.
    /// Useful for setting up test fixtures or default state.
    /// </summary>
    /// <remarks>
    /// This method writes directly into the store without invoking subscription
    /// callbacks. Seeding is intended to run before subscribers are registered;
    /// see the class-level remarks for the rationale.
    /// </remarks>
    public void SetInitialValues(IReadOnlyDictionary<string, object?> values)
    {
        foreach (var (key, value) in values)
            _symbols[key] = value;
        // No callbacks fired — seeding does not notify subscribers.
    }

    /// <summary>
    /// Reads the stored value and converts it to <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Conversion rules (in priority order):</b>
    /// <list type="number">
    ///   <item><description>
    ///     If the symbol path has no stored value (was never written or seeded),
    ///     an <see cref="AdsErrorException"/> with
    ///     <see cref="AdsErrorCode.DeviceSymbolNotFound"/> is thrown — the same
    ///     exception shape a real connection surfaces for an unknown symbol.
    ///   </description></item>
    ///   <item><description>
    ///     If the stored value is <see langword="null"/> and <typeparamref name="T"/>
    ///     is a non-nullable value type, an <see cref="InvalidCastException"/> is thrown
    ///     (actionable: includes symbol, type name).
    ///   </description></item>
    ///   <item><description>
    ///     If the stored value is <see langword="null"/> and <typeparamref name="T"/>
    ///     is a reference type or nullable value type, <see langword="default"/>
    ///     (<see langword="null"/>) is returned.
    ///   </description></item>
    ///   <item><description>
    ///     If the stored value is already a <typeparamref name="T"/> (exact type or
    ///     assignable), it is returned by direct cast.
    ///   </description></item>
    ///   <item><description>
    ///     If the stored value implements <see cref="IConvertible"/> (covers all
    ///     primitive types and <see cref="string"/>),
    ///     <see cref="Convert.ChangeType(object, Type, IFormatProvider)"/> with
    ///     <see cref="CultureInfo.InvariantCulture"/> is attempted. This enables
    ///     numeric widening (<c>int</c>→<c>double</c>) and string-seeded conversions
    ///     (<c>"42"</c>→<c>int</c>, <c>"true"</c>→<c>bool</c>, <c>"3.14"</c>→<c>double</c>).
    ///   </description></item>
    ///   <item><description>
    ///     Otherwise an <see cref="InvalidCastException"/> is thrown with a message
    ///     that includes the symbol path, the requested type, and the actual runtime
    ///     type.
    ///   </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Divergence from <see cref="ReadValueAsync(string, CancellationToken)"/>.</b>
    /// The untyped overload returns <see langword="null"/> for missing symbols (for
    /// backwards compatibility with dashboard/dynamic consumers). This typed overload
    /// throws for missing symbols — a missing symbol has no value to convert and the
    /// caller explicitly requested a concrete type.
    /// </para>
    /// </remarks>
    public Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_symbols.TryGetValue(symbolPath, out var stored))
            // Same exception shape a real connection surfaces for an unknown
            // symbol, so callers (and the contract tests) see one consistent
            // exception type across simulated and real targets.
            throw new AdsErrorException(
                $"Simulated symbol '{symbolPath}' has no stored value; cannot read it as '{typeof(T).Name}'. " +
                $"Write or seed the symbol before performing a typed read.",
                AdsErrorCode.DeviceSymbolNotFound);

        // Conversion (null-handling, direct cast, IConvertible widening, actionable
        // throw on failure) is delegated to the shared converter so a typed read and a
        // typed notification interpret the same stored value identically. The only
        // simulation-specific rule kept here is the missing-symbol AdsErrorException
        // above — a notification, by contrast, always has a value in hand.
        return Task.FromResult(AdsValueConverter.ConvertForRead<T>(stored, symbolPath));
    }

    /// <inheritdoc />
    public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _symbols.TryGetValue(symbolPath, out var value);
        return Task.FromResult(value);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The simulated store holds CLR values with no PLC type information, so
    /// <see cref="AdsValueResult.TypeName"/> and <see cref="AdsValueResult.Category"/> are
    /// inferred from the stored value's runtime type via <see cref="InferPlcType"/>. They are
    /// therefore indicative, not authoritative — a real connection reports the PLC's own
    /// declared type.
    /// <para>
    /// <b>Divergence from <see cref="ReadValueAsync(string, CancellationToken)"/>.</b> The
    /// untyped overload returns <see langword="null"/> for a missing symbol. This method throws
    /// <see cref="AdsErrorException"/> with <see cref="AdsErrorCode.DeviceSymbolNotFound"/>
    /// instead, matching a real connection and matching the simulated typed
    /// <see cref="ReadValueAsync{T}(string, CancellationToken)"/>, which throws for the same
    /// reason (a missing symbol has no metadata to report).
    /// </para>
    /// </remarks>
    public Task<AdsValueResult> ReadValueWithMetadataAsync(string symbolPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_symbols.TryGetValue(symbolPath, out var value))
            throw new AdsErrorException(
                $"Simulated symbol '{symbolPath}' has no stored value; cannot read its metadata.",
                AdsErrorCode.DeviceSymbolNotFound);

        var (typeName, category) = InferPlcType(value);
        return Task.FromResult(AdsValueResult.Success(value, symbolPath, typeName, category));
    }

    /// <summary>
    /// Maps a stored CLR value to a plausible PLC type name and category, so a simulated
    /// metadata read reports the same shape of metadata a real connection would.
    /// </summary>
    /// <remarks>
    /// Internal (rather than private) so other simulated-connection surfaces added later in this
    /// library (for example symbol browsing or notification metadata) and the test project (via
    /// <c>InternalsVisibleTo</c>) can reuse the same inference instead of re-deriving it.
    /// </remarks>
    internal static (string TypeName, string Category) InferPlcType(object? value) => value switch
    {
        null => ("UNKNOWN", "Unknown"),
        bool => ("BOOL", "Primitive"),
        sbyte => ("SINT", "Primitive"),
        byte => ("USINT", "Primitive"),
        short => ("INT", "Primitive"),
        ushort => ("UINT", "Primitive"),
        int => ("DINT", "Primitive"),
        uint => ("UDINT", "Primitive"),
        long => ("LINT", "Primitive"),
        ulong => ("ULINT", "Primitive"),
        float => ("REAL", "Primitive"),
        double => ("LREAL", "Primitive"),
        string => ("STRING", "String"),
        DateTime or DateTimeOffset => ("DT", "Primitive"),
        TimeSpan => ("TIME", "Primitive"),
        System.Collections.IDictionary => ("STRUCT", "Struct"),
        Array => ("ARRAY", "Array"),
        _ => (value.GetType().Name.ToUpperInvariant(), "Unknown"),
    };

    /// <summary>
    /// Writes a typed value. The value is stored boxed; subsequent typed reads will
    /// apply the conversion rules documented on
    /// <see cref="ReadValueAsync{T}(string, CancellationToken)"/>.
    /// </summary>
    public Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct)
        => WriteValueAsync(symbolPath, (object)value!, ct);

    /// <summary>
    /// Writes a value and fires registered callbacks for <paramref name="symbolPath"/>
    /// if the value changed (on-change semantics).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Callbacks are invoked synchronously on the caller's thread immediately after
    /// the value is stored. A callback that throws is caught and logged; it does not
    /// abort the write or suppress other registered callbacks for the same path.
    /// Writing the same value again (by <c>Equals</c>) does not invoke callbacks.
    /// </para>
    /// <para>
    /// <b>Concurrency.</b> The check-then-store is resolved via
    /// <c>ConcurrentDictionary.AddOrUpdate</c>'s
    /// compare-and-swap retry loop: the update factory may run multiple times under contention,
    /// and the captured previous value is overwritten on each invocation, so after AddOrUpdate
    /// returns it holds exactly the value displaced by the winning swap. The writer whose swap
    /// changed the value fires the callback; concurrent writers arriving with the same new value
    /// recapture the already-updated value and do not fire again.
    /// </para>
    /// </remarks>
    public Task WriteValueAsync(string symbolPath, object value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // The update factory runs OUTSIDE any lock and may be invoked multiple times in
        // ConcurrentDictionary's CAS retry loop. capturedPrevious is overwritten on each
        // invocation, so after AddOrUpdate returns it holds the value displaced by the
        // WINNING compare-and-swap — which is exactly the "previous" the change check needs.
        // The factory must therefore stay side-effect-free (capture only).
        object? capturedPrevious = null;
        var isFirstWrite = true;
        _symbols.AddOrUpdate(
            symbolPath,
            addValueFactory: _ => value,
            updateValueFactory: (_, existing) =>
            {
                capturedPrevious = existing;
                isFirstWrite = false;
                return value;
            });

        // On-change: fire only when the value actually changed.
        // First write (path absent) always counts as a change.
        if (isFirstWrite || !Equals(capturedPrevious, value))
            FireCallbacks(symbolPath, value);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Per-symbol results. A missing symbol in simulation yields
    /// <see cref="AdsValueResult.Success(object?, string?)"/> with a <see langword="null"/> value — mirroring the
    /// untyped single-read (<see cref="ReadValueAsync(string, CancellationToken)"/>), which
    /// returns <see langword="null"/> for an unwritten path while a real connection throws
    /// <see cref="AdsErrorException"/>. This simulated/real divergence already exists on the
    /// untyped single-read path; it is kept consistent here and flagged for the
    /// contract suite. Cancellation aborts the whole batch before any result is produced.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, AdsValueResult>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var results = new Dictionary<string, AdsValueResult>();
        foreach (var path in symbolPaths)
        {
            if (results.ContainsKey(path))
                continue;

            try
            {
                var value = await ReadValueAsync(path, ct).ConfigureAwait(false);
                results[path] = AdsValueResult.Success(value, path);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results[path] = AdsValueResult.Failure(ex, path);
            }
        }
        return results;
    }

    /// <summary>
    /// Writes a batch of values and fires registered callbacks per changed symbol
    /// (on-change semantics, same rules as <see cref="WriteValueAsync"/>), returning a per-symbol
    /// result.
    /// </summary>
    /// <remarks>
    /// A non-null simulated write cannot fail, so each such symbol is wrapped in
    /// <see cref="AdsValueResult.Success(object?, string?)"/> (with a <see langword="null"/> value) to keep the
    /// batch contract uniform across simulated and real connections. A null value is rejected
    /// per-symbol with an <see cref="ArgumentNullException"/> wrapped in
    /// <see cref="AdsValueResult.Failure(Exception, string?)"/> — matching the real connection, which cannot write a
    /// null — and is NOT stored. Cancellation aborts the whole batch before any value is stored.
    /// </remarks>
    public Task<IReadOnlyDictionary<string, AdsValueResult>> WriteValuesAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var results = new Dictionary<string, AdsValueResult>();
        foreach (var (path, value) in values)
        {
            // A null is a per-symbol programming error (the real path cannot write null);
            // record it as a failure without touching the symbol store.
            if (value is null)
            {
                results[path] = AdsValueResult.Failure(
                    new ArgumentNullException(
                        $"values[\"{path}\"]", $"Cannot write a null value to symbol '{path}'."),
                    path);
                continue;
            }

            // Same atomic AddOrUpdate pattern as WriteValueAsync — see that method's remarks.
            object? capturedPrevious = null;
            var isFirstWrite = true;
            _symbols.AddOrUpdate(
                path,
                addValueFactory: _ => value,
                updateValueFactory: (_, existing) =>
                {
                    capturedPrevious = existing;
                    isFirstWrite = false;
                    return value;
                });

            // FireCallbacks never rethrows (per-callback exceptions are caught and logged
            // inside the subscriber list), so no try/catch is needed around the loop body.
            if (isFirstWrite || !Equals(capturedPrevious, value))
                FireCallbacks(path, value);

            results[path] = AdsValueResult.Success(null, path);
        }
        return Task.FromResult<IReadOnlyDictionary<string, AdsValueResult>>(results);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Starts at <see cref="AdsState.Run"/> and reflects the most recent
    /// <see cref="WriteControlAsync"/> call immediately.
    /// </remarks>
    public Task<AdsState> GetAdsStateAsync(CancellationToken ct)
        => Task.FromResult(_adsState);

    /// <inheritdoc />
    /// <remarks>Records the requested state so <see cref="GetAdsStateAsync"/> reflects it immediately.</remarks>
    public Task WriteControlAsync(AdsState state, ushort deviceState, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _adsState = state;
        _logger.LogInformation(
            "Simulated WriteControl on {PlcId}: state={State}, deviceState={DeviceState}",
            PlcId, state, deviceState);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reports a synthetic identity so simulated consumers get a well-formed response. The name
    /// is deliberately recognisable as simulated rather than imitating a real runtime.
    /// </remarks>
    public Task<AdsDeviceInfo> GetDeviceInfoAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new AdsDeviceInfo("Simulated ADS Device", "0.0.0"));
    }

    /// <summary>
    /// Registers a callback that fires each time <paramref name="symbolPath"/> is written
    /// with a value that differs from the previously stored value (on-change semantics).
    /// </summary>
    /// <param name="symbolPath">The symbol path to watch.</param>
    /// <param name="cycleTimeMs">
    /// Accepted for interface compatibility with real ADS. Simulation delivers immediately
    /// on change with no throttle — this parameter has no effect.
    /// </param>
    /// <param name="callback">
    /// Invoked synchronously on the writer's thread with (path, newValue). Must be
    /// thread-safe. Exceptions thrown by the callback are caught and logged; they do
    /// not propagate to the writer.
    /// </param>
    /// <param name="ct">Cancels the registration call (not the subscription lifetime).</param>
    /// <returns>
    /// A disposable that unregisters this specific callback. Dispose is idempotent and
    /// thread-safe. Multiple callbacks may be registered for the same path; disposing
    /// one handle does not affect the others.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>On-change semantics.</b> The callback fires only when
    /// <c>!object.Equals(previousValue, newValue)</c>. Writing the same value twice
    /// does not fire a second callback. The first write to a path always counts as a
    /// change (there is no previous value). See the class-level remarks for boxed-type
    /// equality behaviour.
    /// </para>
    /// <para>
    /// <b>Writer-thread delivery.</b> Unlike real ADS — which invokes callbacks on a
    /// dedicated ADS notification thread — simulation invokes callbacks synchronously on
    /// the thread that called <see cref="WriteValueAsync"/> or
    /// <see cref="WriteValuesAsync"/>. This divergence is intentional for simplicity;
    /// callbacks must still be designed to be thread-safe.
    /// </para>
    /// <para>
    /// <b>Seeding silence.</b> <see cref="SetInitialValues"/> does not trigger callbacks.
    /// See the class-level remarks.
    /// </para>
    /// </remarks>
    public Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var list = _subscribers.GetOrAdd(symbolPath, _ => new SubscriberList());
        var registration = list.Add(callback);
        return Task.FromResult<IDisposable>(registration);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Wraps <paramref name="callback"/> with <see cref="TypedCallbackAdapter.Wrap{T}"/>
    /// and delegates to the untyped <see cref="SubscribeAsync(string, int, Action{string, object?}, CancellationToken)"/>. Each notification value
    /// is converted to <typeparamref name="T"/> with the same rules as
    /// <see cref="ReadValueAsync{T}(string, CancellationToken)"/>; a value that fails
    /// conversion (or a null with a non-nullable value-type <typeparamref name="T"/>) is
    /// dropped with a Warning rather than delivered.
    /// </remarks>
    public Task<IDisposable> SubscribeAsync<T>(string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct)
        => SubscribeAsync(symbolPath, cycleTimeMs, TypedCallbackAdapter.Wrap(callback, _logger), ct);

    /// <inheritdoc />
    /// <remarks>
    /// Adapts <paramref name="callback"/> into the untyped shape and delegates to the untyped
    /// <see cref="SubscribeAsync(string, int, Action{string, object?}, CancellationToken)"/>, so it goes through the same subscriber list, the same
    /// on-change rule and the same exception handling as every other simulated subscription.
    /// <para>
    /// The simulated store holds CLR values with no PLC type information, so
    /// <see cref="AdsNotification.TypeName"/> is inferred from the written value's runtime type via
    /// <see cref="InferPlcType"/> — the same inference <see cref="ReadValueWithMetadataAsync"/>
    /// uses, so a notification and a metadata read report the same type for the same value.
    /// </para>
    /// <para>
    /// <see cref="AdsNotification.Timestamp"/> is <see cref="DateTimeOffset.UtcNow"/>: there is no
    /// PLC-reported time to relay, and the simulated write that triggered this callback happened
    /// immediately before it (callbacks fire synchronously on the writer's thread).
    /// </para>
    /// </remarks>
    public Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs,
        Action<AdsNotification> callback, CancellationToken ct)
        => SubscribeAsync(
            symbolPath,
            cycleTimeMs,
            (path, value) =>
            {
                var (typeName, _) = InferPlcType(value);
                callback(new AdsNotification(path, value, typeName, DateTimeOffset.UtcNow));
            },
            ct);

    private void FireCallbacks(string symbolPath, object? newValue)
    {
        if (!_subscribers.TryGetValue(symbolPath, out var list))
            return;

        list.Fire(symbolPath, newValue, _logger);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The simulated store is a flat map of dotted paths, so the symbol tree is derived from the
    /// seeded keys: <c>MAIN.Motor.Speed</c> yields a <c>MAIN</c> container holding a
    /// <c>MAIN.Motor</c> container holding the <c>MAIN.Motor.Speed</c> leaf. Container nodes are
    /// synthetic — they have no stored value — and report type <c>STRUCT</c>. There is no real
    /// browse to bound, so — unlike <see cref="AdsConnection"/> — this completes synchronously and
    /// never consults <see cref="PlcTargetOptions.SymbolBrowseTimeoutMs"/>.
    /// </remarks>
    public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, CancellationToken ct)
        => GetSymbolsAsync(parentPath, includeChildren: true, ct);

    /// <inheritdoc />
    /// <remarks>
    /// Same synthetic-container derivation as
    /// <see cref="GetSymbolsAsync(string?, CancellationToken)"/>; <paramref name="includeChildren"/>
    /// selects whether each returned node carries its nested subtree, matching a real connection.
    /// </remarks>
    public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, bool includeChildren, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var prefix = string.Empty;
        if (!string.IsNullOrEmpty(parentPath))
        {
            var canonicalParent = ResolveStoredCasing(parentPath)
                ?? throw new AdsErrorException($"Symbol '{parentPath}' not found.", AdsErrorCode.DeviceSymbolNotFound);
            prefix = canonicalParent + ".";
        }

        var childNames = _symbols.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.Length > prefix.Length)
            .Select(k => k.Substring(prefix.Length).Split('.')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = childNames
            .Select(name => BuildSymbolInfo(prefix + name, includeChildren))
            .ToList();

        return Task.FromResult<IReadOnlyList<AdsSymbolInfo>>(result);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Matches the same substring rule as a real connection, case-insensitively. Walks every
    /// seeded leaf path plus every synthetic container path above it — see
    /// <see cref="AllPaths"/> — so its cost is proportional to the number of seeded symbols.
    /// </remarks>
    public Task<IReadOnlyList<AdsSymbolInfo>> SearchSymbolsAsync(string pattern, bool includeChildren, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var result = AllPaths()
            .Where(p => p.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => BuildSymbolInfo(p, includeChildren))
            .ToList();

        return Task.FromResult<IReadOnlyList<AdsSymbolInfo>>(result);
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to its as-seeded casing by locating a stored key at or
    /// beneath it, or <see langword="null"/> when nothing is seeded there. PLC symbol paths are
    /// case-insensitive, so a mis-cased caller lookup (e.g. <c>GetSymbolsAsync("main")</c> against
    /// a symbol seeded as <c>MAIN.Speed</c>) must still report <see cref="AdsSymbolInfo.InstancePath"/>
    /// in the casing the symbol was actually seeded with, not echo back whatever the caller typed.
    /// </summary>
    private string? ResolveStoredCasing(string path)
    {
        foreach (var key in _symbols.Keys)
        {
            if (key.Equals(path, StringComparison.OrdinalIgnoreCase))
                return key;
            if (key.Length > path.Length && key[path.Length] == '.' &&
                key.AsSpan(0, path.Length).Equals(path, StringComparison.OrdinalIgnoreCase))
                return key[..path.Length];
        }
        return null;
    }

    /// <summary>Every stored leaf path plus every synthetic container path above it.</summary>
    private IEnumerable<string> AllPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _symbols.Keys)
        {
            var segments = key.Split('.');
            for (var i = 1; i <= segments.Length; i++)
                paths.Add(string.Join('.', segments.Take(i)));
        }
        return paths;
    }

    /// <summary>
    /// Builds symbol metadata for one simulated path, recursing into children on demand. A path
    /// with a stored value is a leaf, mapped via <see cref="InferPlcType"/> (the same inference
    /// <see cref="ReadValueWithMetadataAsync"/> uses); a path with no stored value is a synthetic
    /// <c>STRUCT</c> container implied by deeper seeded paths.
    /// </summary>
    private AdsSymbolInfo BuildSymbolInfo(string path, bool includeChildren)
    {
        var isLeaf = _symbols.TryGetValue(path, out var value);
        var (typeName, category) = isLeaf ? InferPlcType(value) : ("STRUCT", "Struct");

        List<AdsSymbolInfo>? children = null;
        if (includeChildren)
        {
            var prefix = path + ".";
            var childNames = _symbols.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.Length > prefix.Length)
                .Select(k => k.Substring(prefix.Length).Split('.')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (childNames.Count > 0)
                children = childNames.Select(n => BuildSymbolInfo(prefix + n, includeChildren: true)).ToList();
        }

        return new AdsSymbolInfo(path, typeName, category, ByteSize: 0, Comment: null, children);
    }

    void IManagedConnection.Connect() { }
    void IManagedConnection.Disconnect() { }
    Task<bool> IManagedConnection.IsAliveAsync(CancellationToken ct) => Task.FromResult(true);
    void IManagedConnection.ForceDisconnect() { }
    void IManagedConnection.LogSymbolTree(SymbolDumpOptions options) { }

    /// <summary>
    /// Disposes the simulated connection. A no-op: the in-memory store and subscriber
    /// lists hold no unmanaged resources.
    /// </summary>
    public void Dispose() { }

    // -------------------------------------------------------------------------
    // Thread-safe per-path subscriber list.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Holds all callbacks registered for a single symbol path.
    /// A simple lock (not ConcurrentDictionary) is used for the inner list because
    /// the three operations — add, remove, snapshot-and-fire — need to be atomic as
    /// a group. Under concurrent write+dispose the lock ensures a callback is either
    /// included in a fire snapshot (and fires) or absent from it (disposed before
    /// the snapshot was taken), with no torn reads.
    /// </summary>
    private sealed class SubscriberList
    {
        private readonly object _lock = new();
        private readonly Dictionary<long, Action<string, object?>> _callbacks = new();
        private long _nextId;

        /// <summary>Adds a callback and returns a disposable that removes it.</summary>
        public IDisposable Add(Action<string, object?> callback)
        {
            long id;
            lock (_lock)
            {
                id = _nextId++;
                _callbacks[id] = callback;
            }
            return new Registration(this, id);
        }

        /// <summary>Removes the callback with the given id. Idempotent.</summary>
        public void Remove(long id)
        {
            lock (_lock)
                _callbacks.Remove(id);
        }

        /// <summary>
        /// Takes a snapshot of current callbacks under the lock, then invokes each
        /// outside the lock so callbacks cannot deadlock on re-entrant writes.
        /// Exceptions per callback are caught and logged; they do not suppress others.
        /// </summary>
        public void Fire(string path, object? value, ILogger logger)
        {
            Action<string, object?>[] snapshot;
            lock (_lock)
                snapshot = [.. _callbacks.Values];

            foreach (var cb in snapshot)
            {
                try
                {
                    cb(path, value);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Subscription callback for path {Path} threw an exception; notification continues for other subscribers.",
                        path);
                }
            }
        }

        private sealed class Registration(SubscriberList owner, long id) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    owner.Remove(id);
            }
        }
    }
}
