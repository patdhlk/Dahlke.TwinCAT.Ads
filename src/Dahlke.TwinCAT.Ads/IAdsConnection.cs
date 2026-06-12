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

    /// <summary>
    /// Subscribes to value-change notifications for <paramref name="symbolPath"/>,
    /// invoking <paramref name="callback"/> with the symbol path and latest value
    /// each time the PLC reports a change (at most every <paramref name="cycleTimeMs"/>
    /// milliseconds).
    /// </summary>
    /// <param name="symbolPath">The fully-qualified PLC symbol to watch.</param>
    /// <param name="cycleTimeMs">Minimum interval, in milliseconds, between notifications.</param>
    /// <param name="callback">Invoked on each notification with the symbol path and decoded value.</param>
    /// <param name="ct">Cancels the initial registration (not the subscription itself).</param>
    /// <returns>
    /// A handle whose disposal removes the subscription permanently. The awaited
    /// task completes once the subscription has been registered against the current
    /// connection.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Durable across reconnects.</b> The subscription is owned by this stable
    /// <see cref="IAdsConnection"/>, not by the underlying connection it is first
    /// registered on. When the connection is lost and the pool reconnects, the
    /// subscription is automatically re-registered against the new connection — the
    /// returned <see cref="IDisposable"/> stays valid throughout and the
    /// <paramref name="callback"/> resumes firing once a connection is re-established.
    /// Disposing the handle removes the subscription for good: it will not be
    /// re-registered on any future reconnect, and the current underlying
    /// registration is released. Dispose is idempotent and thread-safe.
    /// </para>
    /// <para>
    /// <b>Registration during an outage.</b> If no connection is currently available
    /// when this is called, the registration follows the same wait-then-throw
    /// contract as every other operation: it waits up to
    /// <see cref="PlcTargetOptions.TimeoutMs"/> for a connection to be published and
    /// then registers against it, or throws
    /// <see cref="AdsConnectionUnavailableException"/> if the window elapses first.
    /// </para>
    /// <para>
    /// <b>Callback threading.</b> The <paramref name="callback"/> is invoked on a
    /// background thread owned by the underlying ADS client — never the caller's
    /// thread, and never the thread that awaited this method. After a reconnect the
    /// callback fires from the NEW connection's notification thread. Callbacks must
    /// therefore be thread-safe; they should not block or throw (an exception from a
    /// callback is swallowed and logged by the underlying connection and does not
    /// tear down the subscription). The callback may fire concurrently with, but
    /// never after, disposal completes for the registration it was attached to.
    /// </para>
    /// </remarks>
    Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct);
}
