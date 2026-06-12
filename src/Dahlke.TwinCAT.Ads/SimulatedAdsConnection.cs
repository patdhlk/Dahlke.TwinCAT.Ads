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
/// <b>Subscriptions.</b> Callbacks registered via <see cref="SubscribeAsync"/> fire
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
    private readonly ConcurrentDictionary<string, object?> _symbols = new();

    // Per-path subscriber list. Each entry is a list of (unique id → callback) pairs.
    // ConcurrentDictionary provides thread-safe path lookup; the inner lock guards
    // the list under concurrent subscribe/dispose/fire operations.
    private readonly ConcurrentDictionary<string, SubscriberList> _subscribers = new();

    public string PlcId { get; }
    public string DisplayName { get; }
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
    /// unit tests and the C26 adapter.
    /// </remarks>
#pragma warning disable CS0067 // The event is never used — by design; see remarks.
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
#pragma warning restore CS0067

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
            // symbol, so callers (and the C26 contract tests) see one consistent
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

    public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _symbols.TryGetValue(symbolPath, out var value);
        return Task.FromResult(value);
    }

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
    /// Callbacks are invoked synchronously on the caller's thread immediately after
    /// the value is stored. A callback that throws is caught and logged; it does not
    /// abort the write or suppress other registered callbacks for the same path.
    /// Writing the same value again (by <c>Equals</c>) does not invoke callbacks.
    /// </remarks>
    public Task WriteValueAsync(string symbolPath, object value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var hadOldValue = _symbols.TryGetValue(symbolPath, out var existing);
        _symbols[symbolPath] = value;

        // On-change: fire only when the value actually changed.
        // First write (path absent) always counts as a change.
        if (!hadOldValue || !Equals(existing, value))
            FireCallbacks(symbolPath, value);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Per-symbol results. A missing symbol in simulation yields
    /// <see cref="AdsValueResult.Success"/> with a <see langword="null"/> value — mirroring the
    /// untyped single-read (<see cref="ReadValueAsync(string, CancellationToken)"/>), which
    /// returns <see langword="null"/> for an unwritten path while a real connection throws
    /// <see cref="AdsErrorException"/>. This simulated/real divergence already exists on the
    /// untyped single-read path; it is kept consistent here and flagged for the contract-test
    /// commit (C26). Cancellation aborts the whole batch before any result is produced.
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
    /// <see cref="AdsValueResult.Success"/> (with a <see langword="null"/> value) to keep the
    /// batch contract uniform across simulated and real connections. A null value is rejected
    /// per-symbol with an <see cref="ArgumentNullException"/> wrapped in
    /// <see cref="AdsValueResult.Failure"/> — matching the real connection, which cannot write a
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

            var hadOldValue = _symbols.TryGetValue(path, out var existing);
            _symbols[path] = value;

            // FireCallbacks never rethrows (per-callback exceptions are caught and logged
            // inside the subscriber list), so no try/catch is needed around the loop body.
            if (!hadOldValue || !Equals(existing, value))
                FireCallbacks(path, value);

            results[path] = AdsValueResult.Success(null, path);
        }
        return Task.FromResult<IReadOnlyDictionary<string, AdsValueResult>>(results);
    }

    public Task<AdsState> GetAdsStateAsync(CancellationToken ct)
        => Task.FromResult(AdsState.Run);

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
    /// and delegates to the untyped <see cref="SubscribeAsync"/>. Each notification value
    /// is converted to <typeparamref name="T"/> with the same rules as
    /// <see cref="ReadValueAsync{T}(string, CancellationToken)"/>; a value that fails
    /// conversion (or a null with a non-nullable value-type <typeparamref name="T"/>) is
    /// dropped with a Warning rather than delivered.
    /// </remarks>
    public Task<IDisposable> SubscribeAsync<T>(string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct = default)
        => SubscribeAsync(symbolPath, cycleTimeMs, TypedCallbackAdapter.Wrap(callback, _logger), ct);

    private void FireCallbacks(string symbolPath, object? newValue)
    {
        if (!_subscribers.TryGetValue(symbolPath, out var list))
            return;

        list.Fire(symbolPath, newValue, _logger);
    }

    void IManagedConnection.Connect() { }
    void IManagedConnection.Disconnect() { }
    Task<bool> IManagedConnection.IsAliveAsync(CancellationToken ct) => Task.FromResult(true);
    void IManagedConnection.ForceDisconnect() { }
    void IManagedConnection.LogSymbolTree(SymbolDumpOptions options) { }

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
