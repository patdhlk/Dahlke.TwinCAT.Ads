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
public sealed class SimulatedAdsConnection : IAdsConnection, IManagedConnection
{
    private readonly ILogger<SimulatedAdsConnection> _logger;

    // Value store + fire rule, owned per connection: the store dies with the
    // connection, which is why ForceReconnect is a documented no-op for
    // simulated targets — replacing the connection would wipe values written
    // since startup. The change semantics (first-write-fires, same-value-silent)
    // live in the shared store module.
    private readonly InMemoryPlcStore<string, object?> _store = new(StringComparer.OrdinalIgnoreCase);

    // Written by WriteControlAsync, read by GetAdsStateAsync; volatile gives lock-free
    // cross-thread visibility for this simple flag-like field (same rationale as the
    // facade's _stopped/_state fields).
    private volatile AdsState _adsState = AdsState.Run;

    // Subscriber delivery, owned per connection alongside the store. The
    // mechanics (snapshot-then-fire, per-callback isolation, idempotent
    // disposal) live in the shared registry module; the key comparer matches the
    // store's so subscription casing never has to match the writer's.
    private readonly SubscriberRegistry<string, object?> _subscribers;

    // Seeded PLC method calls, keyed case-insensitively on (path, method) — see RpcKeyComparer.
    // Guarded by locking on the dictionary itself rather than a ConcurrentDictionary: seeding is
    // a test-setup operation, so the contention this would relieve does not exist.
    private readonly Dictionary<(string Path, string Method), Func<object?[], AdsRpcResult>> _rpcHandlers
        = new(RpcKeyComparer.Instance);

    // Seeded enum metadata, keyed case-insensitively on the type name — matches real PLC type
    // name lookup. Guarded by locking on the dictionary itself, like _rpcHandlers: seeding is a
    // test-setup operation, so the contention a ConcurrentDictionary would relieve does not exist.
    private readonly Dictionary<string, IReadOnlyList<AdsEnumMember>> _enumMembers
        = new(StringComparer.OrdinalIgnoreCase);

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
        _subscribers = new SubscriberRegistry<string, object?>(
            StringComparer.OrdinalIgnoreCase,
            onCallbackError: (path, ex) => _logger.LogWarning(ex,
                "Subscription callback for path {Path} threw an exception; notification continues for other subscribers.",
                path));
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
            _store.Seed(key, value);
        // No callbacks fired — the store's Seed cannot signal, by design.
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
    public Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_store.TryRead(symbolPath, out var stored))
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
    public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _store.TryRead(symbolPath, out var value);
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
    public Task<AdsValueResult> ReadValueWithMetadataAsync(string symbolPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_store.TryRead(symbolPath, out var value))
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
    public Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct = default)
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
    public Task WriteValueAsync(string symbolPath, object value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // The store decides (first write, or changed by Equals — the one fire
        // rule, including its CAS-capture concurrency contract); the registry
        // delivers.
        if (_store.Write(symbolPath, value))
            _subscribers.Fire(symbolPath, value);

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
    public async Task<IReadOnlyDictionary<string, AdsValueResult>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct = default)
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

                // Carry the same inferred metadata ReadValueWithMetadataAsync reports. A real
                // connection populates TypeName/Category on every successful batch result, so
                // leaving them null here would show a consumer developing against simulation a
                // case that does not exist on hardware.
                var (typeName, category) = InferPlcType(value);
                results[path] = AdsValueResult.Success(value, path, typeName, category);
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
    public Task<IReadOnlyDictionary<string, AdsValueResult>> WriteValuesAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct = default)
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

            // Same store-decides/registry-delivers step as WriteValueAsync. Fire
            // never rethrows (per-callback exceptions are isolated inside the
            // registry), so no try/catch is needed around the loop body.
            if (_store.Write(path, value))
                _subscribers.Fire(path, value);

            results[path] = AdsValueResult.Success(null, path);
        }
        return Task.FromResult<IReadOnlyDictionary<string, AdsValueResult>>(results);
    }

    /// <summary>
    /// Seeds the result of a simulated PLC method call. Code-first only — configuration is
    /// string-typed and cannot express a handler.
    /// </summary>
    /// <param name="symbolPath">
    /// The instance path the handler answers for, matched case-insensitively like every other
    /// simulated symbol path.
    /// </param>
    /// <param name="methodName">The method name, likewise matched case-insensitively.</param>
    /// <param name="handler">
    /// Invoked with the caller's arguments; its <see cref="AdsRpcResult"/> is what the call
    /// returns. Seeding the same path and method again replaces the previous handler.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public void SetRpcHandler(string symbolPath, string methodName, Func<object?[], AdsRpcResult> handler)
    {
        ArgumentNullException.ThrowIfNull(symbolPath);
        ArgumentNullException.ThrowIfNull(methodName);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_rpcHandlers)
            _rpcHandlers[(symbolPath, methodName)] = handler;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// A simulated connection has no PLC to call, so the answer comes from a handler seeded by
    /// <see cref="SetRpcHandler"/>. There is deliberately NO fallback: an unseeded call THROWS.
    /// A simulated call that appeared to succeed while doing nothing is precisely the failure this
    /// surface exists to make impossible — an acknowledge that no-ops looks identical to one that
    /// worked, which is how the shipped path went weeks without anyone noticing.
    /// </para>
    /// <para>
    /// Path and method are matched case-insensitively, matching real PLC symbol paths and method
    /// names (and the simulated store's own key comparer).
    /// </para>
    /// </remarks>
    public Task<AdsRpcResult> InvokeRpcMethodAsync(
        string symbolPath, string methodName, object?[] parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(symbolPath);
        ArgumentNullException.ThrowIfNull(methodName);
        ArgumentNullException.ThrowIfNull(parameters);

        ct.ThrowIfCancellationRequested();

        Func<object?[], AdsRpcResult>? handler;
        lock (_rpcHandlers)
            _rpcHandlers.TryGetValue((symbolPath, methodName), out handler);

        // Deliberately NOT a null result. A simulated call that appears to succeed while doing
        // nothing is the precise failure this library exists to make impossible.
        if (handler is null)
            throw new InvalidOperationException(
                $"No simulated RPC handler is seeded for '{symbolPath}.{methodName}' on PLC " +
                $"'{PlcId}'. Call SetRpcHandler(\"{symbolPath}\", \"{methodName}\", ...) before " +
                "invoking it.");

        return Task.FromResult(handler(parameters));
    }

    /// <summary>Seeds a PLC enumeration's members for simulated resolution.</summary>
    /// <param name="typeName">
    /// The enum type's name, matched case-insensitively like every other simulated lookup.
    /// </param>
    /// <param name="members">
    /// The members to return for <paramref name="typeName"/>. Seeding the same type name again
    /// replaces the previous members.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public void SetEnumMembers(string typeName, IReadOnlyList<AdsEnumMember> members)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentNullException.ThrowIfNull(members);

        lock (_enumMembers)
            _enumMembers[typeName] = members;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A simulated connection has no PLC type system to browse, so the answer comes from
    /// members seeded by <see cref="SetEnumMembers"/>. There is deliberately NO fallback: an
    /// unseeded type THROWS rather than returning an empty or null list — a simulated call that
    /// appeared to succeed while reporting no members is the same class of defect
    /// <see cref="InvokeRpcMethodAsync"/>'s unseeded-call throw exists to make impossible.
    /// </remarks>
    public Task<IReadOnlyList<AdsEnumMember>> GetEnumMembersAsync(string typeName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<AdsEnumMember>? members;
        lock (_enumMembers)
            _enumMembers.TryGetValue(typeName, out members);

        if (members is null)
            throw new InvalidOperationException(
                $"No simulated enum metadata is seeded for type '{typeName}' on PLC '{PlcId}'. " +
                $"Call SetEnumMembers(\"{typeName}\", ...) before resolving it.");

        return Task.FromResult(members);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Starts at <see cref="AdsState.Run"/> and reflects the most recent
    /// <see cref="WriteControlAsync"/> call immediately.
    /// </remarks>
    public Task<AdsState> GetAdsStateAsync(CancellationToken ct = default)
    {
        // Honours the token like every other member here. It previously did not, which made this
        // the one operation whose cancellation behaviour differed from the in-memory double the
        // contract suite runs the facade against.
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_adsState);
    }

    /// <inheritdoc />
    /// <remarks>Records the requested state so <see cref="GetAdsStateAsync"/> reflects it immediately.</remarks>
    public Task WriteControlAsync(AdsState state, ushort deviceState, CancellationToken ct = default)
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
    public Task<AdsDeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
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
    public Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(_subscribers.Subscribe(symbolPath, callback));
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
    public Task<IDisposable> SubscribeAsync<T>(string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct = default)
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
        Action<AdsNotification> callback, CancellationToken ct = default)
        => SubscribeAsync(
            symbolPath,
            cycleTimeMs,
            (path, value) =>
            {
                var (typeName, _) = InferPlcType(value);
                callback(new AdsNotification(path, value, typeName, DateTimeOffset.UtcNow));
            },
            ct);

    /// <inheritdoc />
    /// <remarks>
    /// The simulated store is a flat map of dotted paths, so the symbol tree is derived from the
    /// seeded keys: <c>MAIN.Motor.Speed</c> yields a <c>MAIN</c> container holding a
    /// <c>MAIN.Motor</c> container holding the <c>MAIN.Motor.Speed</c> leaf. Container nodes are
    /// synthetic — they have no stored value — and report type <c>STRUCT</c>. There is no real
    /// browse to bound, so — unlike <see cref="AdsConnection"/> — this completes synchronously and
    /// never consults <see cref="PlcTargetOptions.SymbolBrowseTimeoutMs"/>.
    /// </remarks>
    public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, CancellationToken ct = default)
        => GetSymbolsAsync(parentPath, includeChildren: true, ct);

    /// <inheritdoc />
    /// <remarks>
    /// Same synthetic-container derivation as
    /// <see cref="GetSymbolsAsync(string?, CancellationToken)"/>; <paramref name="includeChildren"/>
    /// selects whether each returned node carries its nested subtree, matching a real connection.
    /// </remarks>
    public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, bool includeChildren, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(SimulatedSymbolTree.GetSymbols(_store, parentPath, includeChildren));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Matches the same substring rule as a real connection, case-insensitively. Walks every
    /// seeded leaf path plus every synthetic container path above it (see
    /// <see cref="SimulatedSymbolTree"/>), so its cost is proportional to the number of seeded
    /// symbols.
    /// </remarks>
    public Task<IReadOnlyList<AdsSymbolInfo>> SearchSymbolsAsync(string pattern, bool includeChildren, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(SimulatedSymbolTree.Search(_store, pattern, includeChildren));
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

}

/// <summary>Case-insensitive on both halves — PLC symbol paths and method names are.</summary>
internal sealed class RpcKeyComparer : IEqualityComparer<(string Path, string Method)>
{
    public static readonly RpcKeyComparer Instance = new();

    public bool Equals((string Path, string Method) x, (string Path, string Method) y) =>
        string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase)
        && string.Equals(x.Method, y.Method, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string Path, string Method) obj) =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Method));
}
