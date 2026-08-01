using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// The view returned by <see cref="IAdsConnection.WithTimeout"/>: an
/// <see cref="AdsConnectionFacade"/> plus one per-call timeout, forwarding every operation to
/// that facade's timeout-aware internal core.
/// </summary>
/// <remarks>
/// <para>
/// <b>A view, not a connection.</b> It opens nothing, owns nothing and needs no disposal. Every
/// member below delegates to <see cref="_inner"/>, so identity
/// (<see cref="IAdsConnection.PlcId"/>), state, the connection-state event, the underlying
/// transport and the durable-subscription registry are the FACADE's — shared with the unscoped
/// connection and with every other scope over it, not copied. Two scopes over one target are two
/// wrappers around one connection.
/// </para>
/// <para>
/// <b>Why a wrapper rather than a second facade.</b> A facade owns
/// <see cref="System.Threading.Interlocked"/>-mutated fields (its current connection pointer and
/// waiter slot), which a second instance structurally cannot share. Anything that duplicated
/// them would be a second connection to the same target with its own outage state — which is
/// exactly what the stable-facade contract exists to prevent.
/// </para>
/// <para>
/// <b>Scopes replace rather than nest.</b> <see cref="WithTimeout"/> here returns a scope over
/// the same inner facade carrying the NEW timeout, so
/// <c>conn.WithTimeout(a).WithTimeout(b)</c> is bounded by <c>b</c> alone and wrappers never
/// stack to arbitrary depth.
/// </para>
/// </remarks>
internal sealed class TimeoutScopedConnection(AdsConnectionFacade inner, TimeSpan timeout) : IAdsConnection
{
    private readonly AdsConnectionFacade _inner = inner;
    private readonly TimeSpan _timeout = timeout;

    // ---- Identity and state: the inner facade's, unmodified -----------------

    public string PlcId => _inner.PlcId;
    public string DisplayName => _inner.DisplayName;
    public bool IsConnected => _inner.IsConnected;
    public ConnectionState State => _inner.State;

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged
    {
        add => _inner.ConnectionStateChanged += value;
        remove => _inner.ConnectionStateChanged -= value;
    }

    /// <inheritdoc />
    public IAdsConnection WithTimeout(TimeSpan newTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(newTimeout, TimeSpan.Zero);
        return new TimeoutScopedConnection(_inner, newTimeout);
    }

    // ---- Operations: the inner facade's cores, bounded by this scope --------

    public Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct = default)
        => _inner.ReadValueAsync<T>(symbolPath, ct, _timeout);

    public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct = default)
        => _inner.ReadValueAsync(symbolPath, ct, _timeout);

    public Task<AdsValueResult> ReadValueWithMetadataAsync(string symbolPath, CancellationToken ct = default)
        => _inner.ReadValueWithMetadataAsync(symbolPath, ct, _timeout);

    public Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct = default)
        => _inner.WriteValueAsync(symbolPath, value, ct, _timeout);

    public Task WriteValueAsync(string symbolPath, object value, CancellationToken ct = default)
        => _inner.WriteValueAsync(symbolPath, value, ct, _timeout);

    public Task<IReadOnlyDictionary<string, AdsValueResult>> ReadValuesAsync(
        IEnumerable<string> symbolPaths, CancellationToken ct = default)
        => _inner.ReadValuesAsync(symbolPaths, ct, _timeout);

    public Task<IReadOnlyDictionary<string, AdsValueResult>> WriteValuesAsync(
        IReadOnlyDictionary<string, object?> values, CancellationToken ct = default)
        => _inner.WriteValuesAsync(values, ct, _timeout);

    public Task<AdsRpcResult> InvokeRpcMethodAsync(
        string symbolPath, string methodName, object?[] parameters, CancellationToken ct = default)
        => _inner.InvokeRpcMethodAsync(symbolPath, methodName, parameters, ct, _timeout);

    public Task<IReadOnlyList<AdsEnumMember>> GetEnumMembersAsync(string typeName, CancellationToken ct = default)
        => _inner.GetEnumMembersAsync(typeName, ct, _timeout);

    public Task<AdsState> GetAdsStateAsync(CancellationToken ct = default)
        => _inner.GetAdsStateAsync(ct, _timeout);

    public Task<AdsDeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
        => _inner.GetDeviceInfoAsync(ct, _timeout);

    public Task WriteControlAsync(AdsState state, ushort deviceState, CancellationToken ct = default)
        => _inner.WriteControlAsync(state, deviceState, ct, _timeout);

    public Task<IDisposable> SubscribeAsync(
        string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct = default)
        => _inner.SubscribeAsync(symbolPath, cycleTimeMs, callback, ct, _timeout);

    public Task<IDisposable> SubscribeAsync<T>(
        string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct = default)
        => _inner.SubscribeAsync(symbolPath, cycleTimeMs, callback, ct, _timeout);

    public Task<IDisposable> SubscribeAsync(
        string symbolPath, int cycleTimeMs, Action<AdsNotification> callback, CancellationToken ct = default)
        => _inner.SubscribeAsync(symbolPath, cycleTimeMs, callback, ct, _timeout);

    public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolTreeAsync(string? parentPath, CancellationToken ct = default)
        => _inner.GetSymbolTreeAsync(parentPath, ct, _timeout);

    [Obsolete("Use GetSymbolTreeAsync for the same behaviour, or GetSymbolsAsync(parentPath, includeChildren: false, ct) for one level.")]
    public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, CancellationToken ct = default)
        => _inner.GetSymbolTreeAsync(parentPath, ct, _timeout);

    public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(
        string? parentPath, bool includeChildren, CancellationToken ct = default)
        => _inner.GetSymbolsAsync(parentPath, includeChildren, ct, _timeout);

    public Task<IReadOnlyList<AdsSymbolInfo>> SearchSymbolsAsync(
        string pattern, bool includeChildren, CancellationToken ct = default)
        => _inner.SearchSymbolsAsync(pattern, includeChildren, ct, _timeout);
}
