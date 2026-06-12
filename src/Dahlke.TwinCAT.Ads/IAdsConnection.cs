using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

public interface IAdsConnection
{
    string PlcId { get; }
    string DisplayName { get; }
    bool IsConnected { get; }

    /// <summary>
    /// The current connection state for this target — <see cref="ConnectionState.Disconnected"/>,
    /// <see cref="ConnectionState.Connecting"/>, or <see cref="ConnectionState.Connected"/>.
    /// </summary>
    /// <remarks>
    /// This is an observational snapshot, like <see cref="IsConnected"/> but tri-state.
    /// It reflects the most recently published state as of the instant it is read. Dashboards
    /// and monitoring consumers can poll this value; for reactive use subscribe to
    /// <see cref="ConnectionStateChanged"/> instead.
    /// </remarks>
    ConnectionState State { get; }

    /// <summary>
    /// Raised whenever this target transitions between
    /// <see cref="ConnectionState.Disconnected"/>, <see cref="ConnectionState.Connecting"/>,
    /// and <see cref="ConnectionState.Connected"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Handlers are invoked on the pool's background reconnect loop thread, not the thread that
    /// started the pool. Handlers must be thread-safe and must not block; any exception thrown
    /// by a handler is caught and logged at Warning severity and will not interrupt reconnection
    /// or prevent other handlers from being invoked.
    /// </para>
    /// <para>
    /// When <see cref="ConnectionState.Disconnected"/> fires, the underlying connection has
    /// already been removed from the pool and cleared from the facade; subsequent operations
    /// on this <see cref="IAdsConnection"/> will wait up to the configured
    /// <see cref="PlcTargetOptions.TimeoutMs"/> for reconnection before throwing
    /// <see cref="AdsConnectionUnavailableException"/>. Exception: when the transition is
    /// caused by the pool stopping (host shutdown), operations fail fast with
    /// <see cref="AdsConnectionUnavailableException"/> instead of waiting — a connection
    /// will never be published again, so burning the timeout would only delay shutdown.
    /// </para>
    /// </remarks>
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct);
    Task WriteValueAsync(string symbolPath, object value, CancellationToken ct);
    Task<Dictionary<string, object?>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct);
    Task WriteValuesAsync(Dictionary<string, object> values, CancellationToken ct);

    Task<AdsState> GetAdsStateAsync(CancellationToken ct);
    Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct);
}
