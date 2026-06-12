using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Represents a connection to a single PLC target over ADS.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread safety.</b> All members are safe for concurrent use from any thread; operations on a
/// single connection may interleave freely. No operation blocks another. For
/// <see cref="AdsConnection"/> this is guaranteed by the Beckhoff <c>AdsClient</c>, which
/// multiplexes concurrent requests via unique invoke-ids correlated through an internal
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>. For
/// <see cref="SimulatedAdsConnection"/> concurrent writes to the same path are resolved by the
/// store's compare-and-swap; each value change fires callbacks exactly once.
/// </para>
/// <para>
/// <b>Subscription callbacks.</b> Callbacks registered via
/// <see cref="SubscribeAsync(string,int,Action{string,object?},CancellationToken)"/> are invoked
/// on a background thread — never the caller's thread. Callbacks must be thread-safe and must not
/// block; an exception thrown by a callback is caught, logged at Warning severity, and does not
/// interrupt the subscription.
/// </para>
/// </remarks>
public interface IAdsConnection
{
    /// <summary>
    /// The configured identifier of the PLC target this connection serves. Stable for the
    /// connection's lifetime and case-insensitively unique across configured targets.
    /// </summary>
    string PlcId { get; }

    /// <summary>
    /// A human-readable display name for the target, taken from
    /// <see cref="PlcTargetOptions.DisplayName"/>. Intended for logging and dashboards;
    /// not guaranteed unique.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether a live underlying connection exists and reports itself connected at the instant
    /// this is read.
    /// </summary>
    /// <remarks>
    /// Observational only — a hint, not a guard. The operation methods never consult it; they
    /// apply their own wait-then-throw contract. For tri-state status use <see cref="State"/>;
    /// for reactive notification subscribe to <see cref="ConnectionStateChanged"/>.
    /// </remarks>
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
    /// <summary>
    /// Reads several PLC symbols in one call, returning a per-symbol outcome for each.
    /// </summary>
    /// <param name="symbolPaths">
    /// The symbol paths to read. Duplicate paths are de-duplicated — the returned dictionary
    /// has exactly one entry per distinct path.
    /// </param>
    /// <param name="ct">Cancels the whole batch (see remarks).</param>
    /// <returns>
    /// A dictionary keyed by symbol path with one <see cref="AdsValueResult"/> per requested
    /// (distinct) symbol. A readable symbol yields <see cref="AdsValueResult.Success(object?, string?)"/> carrying
    /// its value; an unreadable symbol yields <see cref="AdsValueResult.Failure(Exception, string?)"/> carrying the
    /// originating exception.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="ct"/> is cancelled. Cancellation aborts the ENTIRE batch — it
    /// is NOT recorded as a per-symbol failure. When this is thrown the returned dictionary is
    /// never produced.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// Thrown when the per-target <see cref="PlcTargetOptions.TimeoutMs"/> elapses before the batch
    /// completes, without <paramref name="ct"/> having been cancelled first. The timeout applies to
    /// the whole batch as a single operation — it is NOT a per-symbol failure.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Per-symbol granularity.</b> One bad symbol does not kill the batch: every other symbol
    /// still gets its own result. Inspect each entry's <see cref="AdsValueResult.Succeeded"/>.
    /// </para>
    /// <para>
    /// <b>One round-trip (sum command).</b> All resolvable symbols are read in a single ADS sum
    /// command — one round-trip for the whole batch, not one read per symbol. Duplicate paths are
    /// de-duplicated before the command is issued.
    /// </para>
    /// <para>
    /// <b>Symbol not found.</b> A symbol that cannot be resolved on the PLC is recorded as a
    /// per-symbol <see cref="AdsValueResult.Failure(Exception, string?)"/> carrying an <see cref="AdsErrorException"/>
    /// with <see cref="AdsErrorCode.DeviceSymbolNotFound"/>, before the sum command, and is excluded
    /// from it.
    /// </para>
    /// <para>
    /// <b>Whole-batch timeout/cancellation.</b> Timeout and cancellation apply to the entire batch
    /// as a single operation: caller cancellation throws <see cref="OperationCanceledException"/>,
    /// and the timeout elapsing throws <see cref="TimeoutException"/> — neither is recorded as a
    /// per-symbol failure.
    /// </para>
    /// </remarks>
    Task<IReadOnlyDictionary<string, AdsValueResult>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct);

    /// <summary>
    /// Writes several PLC symbols in one call, returning a per-symbol outcome for each.
    /// </summary>
    /// <param name="values">
    /// The symbol-path → value pairs to write. Because the input is a dictionary, duplicate paths
    /// are impossible — last writer wins is already resolved by the caller's dictionary.
    /// </param>
    /// <param name="ct">Cancels the whole batch (see remarks).</param>
    /// <returns>
    /// A dictionary keyed by symbol path with one <see cref="AdsValueResult"/> per requested
    /// symbol. A successful write yields <see cref="AdsValueResult.Success(object?, string?)"/> with a
    /// <see langword="null"/> value; a failed write yields <see cref="AdsValueResult.Failure(Exception, string?)"/>
    /// carrying the originating exception.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="ct"/> is cancelled. Cancellation aborts the ENTIRE batch — it
    /// is NOT recorded as a per-symbol failure. When this is thrown the returned dictionary is
    /// never produced.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// Thrown when the per-target <see cref="PlcTargetOptions.TimeoutMs"/> elapses before the batch
    /// completes, without <paramref name="ct"/> having been cancelled first. The timeout applies to
    /// the whole batch as a single operation — it is NOT a per-symbol failure.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>One round-trip (sum command).</b> All writable symbols are written in a single ADS sum
    /// command — one round-trip for the whole batch, not one write per symbol. Per-symbol
    /// granularity matches <see cref="ReadValuesAsync"/>: inspect each entry's
    /// <see cref="AdsValueResult.Succeeded"/>.
    /// </para>
    /// <para>
    /// <b>Null values.</b> A <see langword="null"/> value is a per-symbol programming error,
    /// recorded as a <see cref="AdsValueResult.Failure(Exception, string?)"/> (an <see cref="ArgumentNullException"/>)
    /// before the sum command and excluded from it. A symbol that cannot be resolved is recorded as
    /// a per-symbol <see cref="AdsErrorException"/> failure with
    /// <see cref="AdsErrorCode.DeviceSymbolNotFound"/> and likewise excluded.
    /// </para>
    /// <para>
    /// <b>Whole-batch timeout/cancellation.</b> As with <see cref="ReadValuesAsync"/>, timeout and
    /// cancellation apply to the entire batch as a single operation: caller cancellation throws
    /// <see cref="OperationCanceledException"/>, and the timeout elapsing throws
    /// <see cref="TimeoutException"/>.
    /// </para>
    /// </remarks>
    Task<IReadOnlyDictionary<string, AdsValueResult>> WriteValuesAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct);

    /// <summary>
    /// Reads the current ADS state of the target device (for example
    /// <see cref="AdsState.Run"/>, <see cref="AdsState.Stop"/>, or
    /// <see cref="AdsState.Config"/>).
    /// </summary>
    /// <param name="ct">
    /// Used to cancel the operation. Cancellation and the per-target
    /// <see cref="PlcTargetOptions.TimeoutMs"/> are honored with the same semantics as
    /// <see cref="ReadValueAsync(string, CancellationToken)"/>.
    /// </param>
    /// <returns>The device's current <see cref="AdsState"/>.</returns>
    /// <exception cref="AdsErrorException">Thrown when the ADS state read reports a non-success error code.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    /// <exception cref="TimeoutException">
    /// Thrown when the per-target <see cref="PlcTargetOptions.TimeoutMs"/> elapses before the
    /// read completes, without <paramref name="ct"/> having been cancelled first.
    /// </exception>
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
    Task<IDisposable> SubscribeAsync<T>(string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct);
}
