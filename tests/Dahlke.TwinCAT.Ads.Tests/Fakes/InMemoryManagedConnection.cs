using System.Collections.Concurrent;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Tests.Fakes;

/// <summary>
/// A focused, store-backed <see cref="IManagedConnection"/> test double whose DATA PLANE
/// mirrors the documented <see cref="IAdsConnection"/> contract — independently of
/// <see cref="SimulatedAdsConnection"/>.
/// </summary>
/// <remarks>
/// <para>
/// This double exists so the C26 contract suite can exercise the
/// <see cref="AdsConnectionFacade"/> plumbing (snapshot-then-route, durable subscriptions)
/// against a managed connection that honours the SAME documented semantics the
/// <see cref="SimulatedAdsConnection"/> honours, WITHOUT sharing its implementation. The two
/// data planes are deliberately separate code: the contract suite runs one shared behavioural
/// spec against both, so if either drifts from the documented contract a contract [Fact] fails.
/// The only production code reused here is <see cref="AdsValueConverter"/> — the converter IS
/// the documented conversion contract (direct cast, <see cref="IConvertible"/> widening,
/// invariant-culture string parsing), so re-implementing it would test a different spec, not
/// the real one.
/// </para>
/// <para>
/// <b>Semantics mirrored</b> (see <see cref="IAdsConnection"/> XML docs for the authoritative
/// statements):
/// </para>
/// <list type="bullet">
///   <item><description>
///     Untyped read of a never-written path returns <see langword="null"/>; untyped
///     write→read round-trips the boxed value.
///   </description></item>
///   <item><description>
///     Typed read converts via <see cref="AdsValueConverter.ConvertForRead{T}"/>; a missing
///     symbol throws <see cref="AdsErrorException"/> with
///     <see cref="AdsErrorCode.DeviceSymbolNotFound"/>; a conversion failure throws
///     <see cref="InvalidCastException"/>.
///   </description></item>
///   <item><description>
///     Batch read yields one <see cref="AdsValueResult"/> per distinct path; a missing symbol
///     yields <see cref="AdsValueResult.Success"/> with a <see langword="null"/> value (the
///     documented IN-MEMORY/sim semantic — the real connection diverges; see the contract class
///     docs).
///   </description></item>
///   <item><description>
///     Batch write yields per-symbol <see cref="AdsValueResult.Success"/>; a
///     <see langword="null"/> value yields a per-symbol
///     <see cref="AdsValueResult.Failure"/> carrying an <see cref="ArgumentNullException"/> and
///     is not stored.
///   </description></item>
///   <item><description>
///     Subscriptions fire on CHANGED writes only (<c>!Equals(old, new)</c>); same-value writes
///     do not fire; the first write to a path always fires. Disposing a registration stops
///     delivery; dispose is idempotent; multiple subscribers on a path are independent.
///   </description></item>
/// </list>
/// <para>
/// <b>Divergences from <see cref="SimulatedAdsConnection"/> that DON'T matter to the contract:</b>
/// callbacks fire on the writer's thread (same as sim); cycle time is ignored (same as sim).
/// The contract suite asserts only observable outcomes, not threading, so these are immaterial.
/// </para>
/// <para>
/// Lifecycle members (<see cref="Connect"/>, <see cref="Disconnect"/>,
/// <see cref="IsAliveAsync"/>, <see cref="ForceDisconnect"/>, <see cref="LogSymbolTree"/>) are
/// no-ops: the contract suite drives this double via <see cref="AdsConnectionFacade.SetCurrent"/>,
/// not the pool loop.
/// </para>
/// </remarks>
internal sealed class InMemoryManagedConnection : IManagedConnection
{
    private readonly ConcurrentDictionary<string, object?> _symbols = new();
    private readonly ConcurrentDictionary<string, SubscriberList> _subscribers = new();

    public InMemoryManagedConnection(string plcId = "plc1", string displayName = "In-Memory PLC")
    {
        PlcId = plcId;
        DisplayName = displayName;
    }

    public string PlcId { get; }
    public string DisplayName { get; }

    // Settable so the contract harness can present a connected connection to the facade
    // (the facade's SetCurrent path and IsConnected observation both read this).
    public bool IsConnected { get; set; } = true;

    public ConnectionState State => ConnectionState.Connected;

#pragma warning disable CS0067 // Never raised — this double has no lifecycle transitions.
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
#pragma warning restore CS0067

    // ---- Reads -----------------------------------------------------------

    public Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_symbols.TryGetValue(symbolPath, out var stored))
            throw new AdsErrorException(
                $"In-memory symbol '{symbolPath}' has no stored value; cannot read it as '{typeof(T).Name}'.",
                AdsErrorCode.DeviceSymbolNotFound);

        return Task.FromResult(AdsValueConverter.ConvertForRead<T>(stored, symbolPath));
    }

    public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _symbols.TryGetValue(symbolPath, out var value);
        return Task.FromResult(value);
    }

    // ---- Writes ----------------------------------------------------------

    public Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct)
        => WriteValueAsync(symbolPath, (object)value!, ct);

    public Task WriteValueAsync(string symbolPath, object value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        StoreAndFire(symbolPath, value);
        return Task.CompletedTask;
    }

    // ---- Batch -----------------------------------------------------------

    public Task<IReadOnlyDictionary<string, AdsValueResult>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var results = new Dictionary<string, AdsValueResult>();
        foreach (var path in symbolPaths)
        {
            if (results.ContainsKey(path))
                continue;

            // Missing symbol → Success(null), mirroring the untyped single-read and the
            // documented in-memory/sim batch semantic.
            _symbols.TryGetValue(path, out var value);
            results[path] = AdsValueResult.Success(value, path);
        }
        return Task.FromResult<IReadOnlyDictionary<string, AdsValueResult>>(results);
    }

    public Task<IReadOnlyDictionary<string, AdsValueResult>> WriteValuesAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var results = new Dictionary<string, AdsValueResult>();
        foreach (var (path, value) in values)
        {
            if (value is null)
            {
                results[path] = AdsValueResult.Failure(
                    new ArgumentNullException(
                        $"values[\"{path}\"]", $"Cannot write a null value to symbol '{path}'."),
                    path);
                continue;
            }

            StoreAndFire(path, value);
            results[path] = AdsValueResult.Success(null, path);
        }
        return Task.FromResult<IReadOnlyDictionary<string, AdsValueResult>>(results);
    }

    public Task<AdsState> GetAdsStateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(AdsState.Run);
    }

    // ---- Subscriptions ---------------------------------------------------

    public Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var list = _subscribers.GetOrAdd(symbolPath, _ => new SubscriberList());
        return Task.FromResult(list.Add(callback));
    }

    public Task<IDisposable> SubscribeAsync<T>(string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct = default)
        => SubscribeAsync(symbolPath, cycleTimeMs, TypedCallbackAdapter.Wrap(callback, logger: null), ct);

    /// <summary>
    /// Stores <paramref name="value"/> at <paramref name="symbolPath"/> and fires subscribers
    /// when the value changed (on-change semantics). The first write to a path always fires.
    /// </summary>
    private void StoreAndFire(string symbolPath, object? value)
    {
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

        if (isFirstWrite || !Equals(capturedPrevious, value))
        {
            if (_subscribers.TryGetValue(symbolPath, out var list))
                list.Fire(symbolPath, value);
        }
    }

    // ---- Lifecycle no-ops ------------------------------------------------

    void IManagedConnection.Connect() => IsConnected = true;
    void IManagedConnection.Disconnect() => IsConnected = false;
    Task<bool> IManagedConnection.IsAliveAsync(CancellationToken ct) => Task.FromResult(IsConnected);
    void IManagedConnection.ForceDisconnect() => IsConnected = false;
    void IManagedConnection.LogSymbolTree(SymbolDumpOptions options) { }

    public void Dispose() => IsConnected = false;

    /// <summary>
    /// Thread-safe per-path subscriber list. A snapshot is taken under the lock, then each
    /// callback is invoked outside the lock; a throwing callback is swallowed so it cannot
    /// abort the write or suppress other subscribers (matching the documented contract).
    /// </summary>
    private sealed class SubscriberList
    {
        private readonly object _lock = new();
        private readonly Dictionary<long, Action<string, object?>> _callbacks = new();
        private long _nextId;

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

        private void Remove(long id)
        {
            lock (_lock)
                _callbacks.Remove(id);
        }

        public void Fire(string path, object? value)
        {
            Action<string, object?>[] snapshot;
            lock (_lock)
                snapshot = [.. _callbacks.Values];

            foreach (var cb in snapshot)
            {
                try { cb(path, value); }
                catch { /* swallowed: a callback must not abort the write or suppress others. */ }
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
