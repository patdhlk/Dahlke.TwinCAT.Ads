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
/// This double exists so the contract suite can exercise the
/// <see cref="AdsConnectionFacade"/> plumbing (snapshot-then-route, durable subscriptions)
/// against a managed connection with test-controllable lifecycle state. Its data plane
/// composes the SAME shared modules as <see cref="SimulatedAdsConnection"/> —
/// <see cref="InMemoryPlcStore{TKey, TValue}"/>, <see cref="SubscriberRegistry{TKey, TValue}"/>,
/// <see cref="SimulatedSymbolTree"/>, <see cref="AdsValueConverter"/> — so the store, fire-rule,
/// delivery, tree and conversion semantics are ONE implementation, pinned by those modules'
/// own unit tests; the contract suite pins the adapter glue (exception shapes, batch
/// semantics, metadata inference) on both harnesses.
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
    private readonly InMemoryPlcStore<string, object?> _store = new(StringComparer.OrdinalIgnoreCase);
    private readonly SubscriberRegistry<string, object?> _subscribers = new(StringComparer.OrdinalIgnoreCase);

    // Written by WriteControlAsync, read by GetAdsStateAsync — volatile mirrors
    // SimulatedAdsConnection's equivalent field so the contract fact holds here too.
    private volatile AdsState _adsState = AdsState.Run;

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

    // ---- Reads -----------------------------------------------------------

    public Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();

        if (!_store.TryRead(symbolPath, out var stored))
            throw new AdsErrorException(
                $"In-memory symbol '{symbolPath}' has no stored value; cannot read it as '{typeof(T).Name}'.",
                AdsErrorCode.DeviceSymbolNotFound);

        return Task.FromResult(AdsValueConverter.ConvertForRead<T>(stored, symbolPath));
    }

    public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        _store.TryRead(symbolPath, out var value);
        return Task.FromResult(value);
    }

    /// <summary>
    /// Mirrors <see cref="SimulatedAdsConnection.ReadValueWithMetadataAsync"/>'s documented
    /// semantics: throws for a missing symbol (unlike the untyped <see cref="ReadValueAsync(string, CancellationToken)"/>
    /// above), and infers <see cref="AdsValueResult.TypeName"/>/<see cref="AdsValueResult.Category"/>
    /// via <see cref="SimulatedAdsConnection.InferPlcType"/> — the same inference the sim uses, reused
    /// directly (like <see cref="AdsValueConverter"/> above) because it IS the documented mapping
    /// spec, not something to re-implement and risk drifting from.
    /// </summary>
    public Task<AdsValueResult> ReadValueWithMetadataAsync(string symbolPath, CancellationToken ct, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();

        if (!_store.TryRead(symbolPath, out var value))
            throw new AdsErrorException(
                $"In-memory symbol '{symbolPath}' has no stored value; cannot read its metadata.",
                AdsErrorCode.DeviceSymbolNotFound);

        var (typeName, category) = SimulatedAdsConnection.InferPlcType(value);
        return Task.FromResult(AdsValueResult.Success(value, symbolPath, typeName, category));
    }

    // ---- Writes ----------------------------------------------------------

    public Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct, TimeSpan? timeout = null)
        => WriteValueAsync(symbolPath, (object)value!, ct);

    public Task WriteValueAsync(string symbolPath, object value, CancellationToken ct, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        if (_store.Write(symbolPath, value))
            _subscribers.Fire(symbolPath, value);
        return Task.CompletedTask;
    }

    // ---- Batch -----------------------------------------------------------

    public Task<IReadOnlyDictionary<string, AdsValueResult>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        var results = new Dictionary<string, AdsValueResult>();
        foreach (var path in symbolPaths)
        {
            if (results.ContainsKey(path))
                continue;

            // Missing symbol → Success(null), mirroring the untyped single-read and the
            // documented in-memory/sim batch semantic. Type metadata is carried either way, as a
            // real connection carries it — InferPlcType maps a null to ("UNKNOWN", "Unknown")
            // rather than leaving the fields null.
            _store.TryRead(path, out var value);
            var (typeName, category) = SimulatedAdsConnection.InferPlcType(value);
            results[path] = AdsValueResult.Success(value, path, typeName, category);
        }
        return Task.FromResult<IReadOnlyDictionary<string, AdsValueResult>>(results);
    }

    public Task<IReadOnlyDictionary<string, AdsValueResult>> WriteValuesAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct, TimeSpan? timeout = null)
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

            if (_store.Write(path, value))
                _subscribers.Fire(path, value);
            results[path] = AdsValueResult.Success(null, path);
        }
        return Task.FromResult<IReadOnlyDictionary<string, AdsValueResult>>(results);
    }

    public Task<AdsState> GetAdsStateAsync(CancellationToken ct, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_adsState);
    }

    /// <summary>
    /// Mirrors <see cref="SimulatedAdsConnection.InvokeRpcMethodAsync"/>'s argument validation —
    /// the part of the RPC surface the shared contract suite pins — and then refuses the call.
    /// This double has no handler table to seed, and a call that appeared to succeed while doing
    /// nothing is the exact defect the RPC surface exists to prevent, so the refusal is loud and
    /// names the path and method rather than returning an empty result.
    /// </summary>
    public Task<AdsRpcResult> InvokeRpcMethodAsync(string symbolPath, string methodName, object?[] parameters, CancellationToken ct, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(symbolPath);
        ArgumentNullException.ThrowIfNull(methodName);
        ArgumentNullException.ThrowIfNull(parameters);

        ct.ThrowIfCancellationRequested();

        throw new InvalidOperationException(
            $"This in-memory double cannot invoke '{symbolPath}.{methodName}' — it has no RPC " +
            "handler surface. Use SimulatedAdsConnection.SetRpcHandler for a seedable simulated call.");
    }

    /// <summary>
    /// Mirrors <see cref="SimulatedAdsConnection.InvokeRpcMethodAsync"/>'s refusal shape above:
    /// this double has no PLC type system and no seedable enum-metadata table, so it refuses
    /// rather than returning a plausible-looking empty list.
    /// </summary>
    public Task<IReadOnlyList<AdsEnumMember>> GetEnumMembersAsync(string typeName, CancellationToken ct, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        ct.ThrowIfCancellationRequested();

        throw new InvalidOperationException(
            $"This in-memory double cannot resolve enum type '{typeName}' — it has no type-system " +
            "surface. Use SimulatedAdsConnection.SetEnumMembers for a seedable simulated type.");
    }

    /// <summary>
    /// Mirrors <see cref="SimulatedAdsConnection.WriteControlAsync"/>'s observable-state
    /// semantics: records the requested state so <see cref="GetAdsStateAsync"/> reflects it
    /// immediately — genuinely implemented (not a throwing stub) because the shared contract
    /// suite exercises this double's <c>WriteControlAsync</c>-then-<c>GetAdsStateAsync</c>
    /// round-trip via <see cref="AdsConnectionFacade"/>.
    /// </summary>
    public Task WriteControlAsync(AdsState state, ushort deviceState, CancellationToken ct, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        _adsState = state;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Mirrors <see cref="SimulatedAdsConnection.GetDeviceInfoAsync"/>'s documented synthetic-
    /// identity semantics — a well-formed, recognisably-not-real name and version, since this
    /// double has no PLC runtime to read from.
    /// </summary>
    public Task<AdsDeviceInfo> GetDeviceInfoAsync(CancellationToken ct, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new AdsDeviceInfo("In-Memory ADS Device", "0.0.0"));
    }

    // ---- Subscriptions ---------------------------------------------------

    public Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_subscribers.Subscribe(symbolPath, callback));
    }

    public Task<IDisposable> SubscribeAsync<T>(string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct, TimeSpan? timeout = null)
        => SubscribeAsync(symbolPath, cycleTimeMs, TypedCallbackAdapter.Wrap(callback, logger: null), ct);

    /// <summary>
    /// Mirrors <see cref="SimulatedAdsConnection"/>'s documented notification-metadata semantics:
    /// the type name is inferred from the written value via
    /// <see cref="SimulatedAdsConnection.InferPlcType"/> (reused as the documented mapping spec,
    /// like everywhere else in this double), and the timestamp is the moment of the write, since
    /// this double has no PLC clock. Genuinely implemented — the shared contract suite subscribes
    /// through the facade to THIS overload and asserts all four fields.
    /// </summary>
    public Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<AdsNotification> callback, CancellationToken ct, TimeSpan? timeout = null)
        => SubscribeAsync(
            symbolPath,
            cycleTimeMs,
            (path, value) =>
            {
                var (typeName, _) = SimulatedAdsConnection.InferPlcType(value);
                callback(new AdsNotification(path, value, typeName, DateTimeOffset.UtcNow));
            },
            ct);

    // ---- Symbol browsing --------------------------------------------------

    /// <summary>
    /// Mirrors <see cref="SimulatedAdsConnection.GetSymbolsAsync"/>'s documented tree-from-dotted-
    /// paths semantics — deliberately re-implemented rather than shared (same rationale as the
    /// rest of this double's data plane; see the class remarks), except for
    /// <see cref="SimulatedAdsConnection.InferPlcType"/>, which is reused directly as the
    /// documented mapping spec.
    /// </summary>
    public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolTreeAsync(string? parentPath, CancellationToken ct, TimeSpan? timeout = null)
        => GetSymbolsAsync(parentPath, includeChildren: true, ct);

    /// <inheritdoc cref="GetSymbolsAsync(string?, CancellationToken)"/>
    public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, bool includeChildren, CancellationToken ct, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(SimulatedSymbolTree.GetSymbols(_store, parentPath, includeChildren));
    }

    /// <inheritdoc cref="GetSymbolsAsync"/>
    public Task<IReadOnlyList<AdsSymbolInfo>> SearchSymbolsAsync(string pattern, bool includeChildren, CancellationToken ct, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(SimulatedSymbolTree.Search(_store, pattern, includeChildren));
    }

    // ---- Lifecycle no-ops ------------------------------------------------

    void IManagedConnection.Connect() => IsConnected = true;
    void IManagedConnection.Disconnect() => IsConnected = false;
    Task<bool> IManagedConnection.IsAliveAsync(CancellationToken ct) => Task.FromResult(IsConnected);
    void IManagedConnection.ForceDisconnect() => IsConnected = false;
    void IManagedConnection.LogSymbolTree(SymbolDumpOptions options) { }

    public void Dispose() => IsConnected = false;

}
