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

    /// <summary>
    /// Reads the current value of a PLC symbol and returns it as <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">
    /// The expected .NET type of the symbol value. For numeric types, widening conversions are
    /// applied automatically (e.g. a PLC <c>INT</c> stored as <c>int</c> can be read as
    /// <c>double</c>). String-encoded values seeded in simulation are converted via
    /// <see cref="System.Convert.ChangeType(object, Type, System.IFormatProvider)"/> with
    /// <see cref="System.Globalization.CultureInfo.InvariantCulture"/> (e.g. <c>"42"</c>→<c>int</c>,
    /// <c>"true"</c>→<c>bool</c>, <c>"3.14"</c>→<c>double</c>).
    /// </typeparam>
    /// <param name="symbolPath">The fully-qualified PLC symbol path (e.g. <c>MAIN.Counter</c>).</param>
    /// <param name="ct">
    /// Used to cancel the operation. When the caller's token fires an
    /// <see cref="OperationCanceledException"/> is thrown with that token as the source, allowing
    /// the caller to distinguish cancellation from timeout.
    /// </param>
    /// <returns>The symbol value converted to <typeparamref name="T"/>.</returns>
    /// <exception cref="InvalidCastException">
    /// Thrown when the symbol's runtime value cannot be converted to <typeparamref name="T"/>.
    /// The message includes the symbol path, the requested type, and the actual runtime type to
    /// aid diagnosis.
    /// </exception>
    /// <exception cref="AdsErrorException">
    /// Thrown when the symbol is not found (<see cref="AdsErrorCode.DeviceSymbolNotFound"/>) or
    /// when the ADS read operation itself reports a non-success error code.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="ct"/> is cancelled before or during the read. The exception's
    /// <see cref="OperationCanceledException.CancellationToken"/> matches <paramref name="ct"/>.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// Thrown when the per-target <see cref="PlcTargetOptions.TimeoutMs"/> elapses before the
    /// read completes, without <paramref name="ct"/> having been cancelled first. This lets callers
    /// distinguish a hardware/network timeout from an intentional cancellation.
    /// </exception>
    /// <remarks>
    /// This is the preferred overload for compile-time-typed access to PLC values. For
    /// runtime-typed or polymorphic scenarios where the target type is not known at compile time,
    /// use <see cref="ReadValueAsync(string, CancellationToken)"/> — the dynamic escape hatch.
    /// Cancellation, timeout, and ADS error semantics are identical between both overloads.
    /// </remarks>
    Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct);

    /// <summary>
    /// Reads the current value of a PLC symbol identified by <paramref name="symbolPath"/>.
    /// </summary>
    /// <param name="symbolPath">The fully-qualified PLC symbol path (e.g. <c>MAIN.Counter</c>).</param>
    /// <param name="ct">
    /// Used to cancel the operation. When the caller's token fires an
    /// <see cref="OperationCanceledException"/> is thrown with that token as the source, allowing
    /// the caller to distinguish cancellation from timeout.
    /// </param>
    /// <returns>
    /// The symbol value marshaled to a .NET object — a boxed primitive for scalar symbols, a
    /// dynamic object for struct/array symbols — matching the same value shapes produced by the
    /// synchronous read path.
    /// Returns <see langword="null"/> only for simulated connections where the path has never been
    /// written; real PLC reads throw on unknown symbols.
    /// </returns>
    /// <exception cref="AdsErrorException">
    /// Thrown when the symbol is not found (<see cref="AdsErrorCode.DeviceSymbolNotFound"/>) or
    /// when the ADS read operation itself reports a non-success error code.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="ct"/> is cancelled before or during the read. The exception's
    /// <see cref="OperationCanceledException.CancellationToken"/> matches <paramref name="ct"/>.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// Thrown when the per-target <see cref="PlcTargetOptions.TimeoutMs"/> elapses before the
    /// read completes, without <paramref name="ct"/> having been cancelled first. This lets callers
    /// distinguish a hardware/network timeout from an intentional cancellation.
    /// </exception>
    /// <remarks>
    /// <b>Dynamic escape hatch.</b> Use this overload when the target type is not known at compile
    /// time (e.g. generic dashboards, reflection-driven serialisation). For all other scenarios
    /// prefer <see cref="ReadValueAsync{T}(string, CancellationToken)"/>, which provides
    /// compile-time type safety and actionable conversion errors.
    /// </remarks>
    Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="value"/> to the PLC symbol identified by <paramref name="symbolPath"/>.
    /// </summary>
    /// <typeparam name="T">The compile-time type of the value to write.</typeparam>
    /// <param name="symbolPath">The fully-qualified PLC symbol path.</param>
    /// <param name="value">The value to write. Must be compatible with the symbol's PLC type.</param>
    /// <param name="ct">
    /// Used to cancel the operation. Cancellation and per-target timeout are both honored;
    /// see <see cref="ReadValueAsync{T}(string, CancellationToken)"/> for the exception semantics —
    /// the same rules apply here.
    /// </param>
    /// <exception cref="AdsErrorException">
    /// Thrown when the symbol does not exist or the ADS write fails.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    /// <exception cref="TimeoutException">
    /// Thrown when the per-target <see cref="PlcTargetOptions.TimeoutMs"/> elapses before the
    /// write completes, without <paramref name="ct"/> having been cancelled first.
    /// </exception>
    /// <remarks>
    /// This is the preferred overload for compile-time-typed writes. Overload resolution binds
    /// <c>WriteValueAsync("path", 42, ct)</c> to <c>T=int</c> automatically. For runtime-typed
    /// writes use <see cref="WriteValueAsync(string, object, CancellationToken)"/> — the dynamic
    /// escape hatch.
    /// </remarks>
    Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="value"/> to the PLC symbol identified by <paramref name="symbolPath"/>.
    /// </summary>
    /// <param name="symbolPath">The fully-qualified PLC symbol path.</param>
    /// <param name="value">The value to write. Must be compatible with the symbol's PLC type.</param>
    /// <param name="ct">
    /// Used to cancel the operation. Cancellation and per-target timeout are both honored;
    /// see <see cref="ReadValueAsync(string, CancellationToken)"/> for the exception semantics — the same rules apply here.
    /// </param>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    /// <exception cref="TimeoutException">
    /// Thrown when the per-target <see cref="PlcTargetOptions.TimeoutMs"/> elapses before the
    /// write completes, without <paramref name="ct"/> having been cancelled first.
    /// </exception>
    /// <remarks>
    /// <b>Dynamic escape hatch.</b> Use this overload when the value type is not known at compile
    /// time (e.g. generic dispatch, configuration-driven writes). For all other scenarios prefer
    /// <see cref="WriteValueAsync{T}(string, T, CancellationToken)"/>.
    /// </remarks>
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

    /// <summary>
    /// Subscribes to value-change notifications for <paramref name="symbolPath"/>, invoking <paramref name="callback"/> with the value converted to <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// Durability and threading semantics are identical to the untyped overload:
    /// subscriptions registered through the durable facade survive reconnects and are
    /// automatically re-registered on the new underlying connection.
    ///
    /// Each notification value is converted to <typeparamref name="T"/> using the same
    /// rules as <see cref="ReadValueAsync{T}"/>
    /// (<see cref="System.Convert.ChangeType(object,System.Type,System.IFormatProvider)"/>
    /// with <see cref="System.Globalization.CultureInfo.InvariantCulture"/>).
    ///
    /// A value that fails conversion is DROPPED: a Warning is logged and the callback is
    /// NOT invoked for that notification.  Choose <typeparamref name="T"/> to match the
    /// PLC symbol's type to avoid silent drops.
    ///
    /// A <see langword="null"/> notification value with a value-type <typeparamref name="T"/>
    /// is also dropped (same Warning rule).  A <see langword="null"/> value with a
    /// reference or nullable <typeparamref name="T"/> invokes the callback with
    /// <see langword="null"/>.
    /// </remarks>
    Task<IDisposable> SubscribeAsync<T>(string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct = default);
}
