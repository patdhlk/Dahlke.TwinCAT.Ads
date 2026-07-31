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
    /// Reads a PLC symbol and returns its decoded value together with the symbol's PLC type
    /// metadata.
    /// </summary>
    /// <param name="symbolPath">The fully-qualified PLC symbol path.</param>
    /// <param name="ct">Cancels the operation; per-target timeout applies as elsewhere.</param>
    /// <returns>
    /// A successful <see cref="AdsValueResult"/> whose <see cref="AdsValueResult.Value"/> is a
    /// neutral tree — a boxed primitive for scalars, an
    /// <c>IReadOnlyDictionary&lt;string, object?&gt;</c> for structs, function blocks and unions,
    /// and an <c>object?[]</c> for arrays — with <see cref="AdsValueResult.TypeName"/> and
    /// <see cref="AdsValueResult.Category"/> populated.
    /// </returns>
    /// <exception cref="AdsErrorException">
    /// Thrown when the symbol is not found (<see cref="AdsErrorCode.DeviceSymbolNotFound"/>) or
    /// the read reports a non-success error code. Unlike the batch
    /// <see cref="ReadValuesAsync"/>, a single failed read throws rather than returning a
    /// failed result.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    /// <exception cref="TimeoutException">
    /// Thrown when <see cref="PlcTargetOptions.TimeoutMs"/> elapses first.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Prefer this over <see cref="ReadValueAsync(string, CancellationToken)"/> when the caller
    /// needs to report the PLC type alongside the value, or needs struct and array values as a
    /// serializer-friendly tree rather than a TwinCAT dynamic object.
    /// </para>
    /// <para>
    /// <b>Four categories are NOT decoded into a neutral tree</b> and reach the caller as whatever
    /// Beckhoff's value factory produced: <c>Alias</c> (when it aliases a struct or array),
    /// <c>Program</c>, <c>Pointer</c> and <c>Reference</c>. A caller serialising values generically
    /// should treat a result whose <see cref="AdsValueResult.Category"/> is one of those as opaque
    /// rather than assuming a primitive. Every other category — primitives, strings, enums,
    /// sub-ranges, structs, function blocks, unions and arrays — decodes as described above.
    /// </para>
    /// </remarks>
    Task<AdsValueResult> ReadValueWithMetadataAsync(string symbolPath, CancellationToken ct);

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
    /// <para>
    /// <b>Dynamic escape hatch.</b> Use this overload when the value type is not known at compile
    /// time (e.g. generic dispatch, configuration-driven writes). For all other scenarios prefer
    /// <see cref="WriteValueAsync{T}(string, T, CancellationToken)"/>.
    /// </para>
    /// <para>
    /// <paramref name="value"/> is non-nullable by design: a single write must supply a value, so a
    /// missing one is a compile-time error rather than a runtime fault. This deliberately differs from
    /// <see cref="WriteValuesAsync"/>, whose dictionary values are <see langword="object?"/> because a
    /// batch records a per-symbol <see cref="AdsValueResult.Failure(Exception)"/> for a null instead of
    /// aborting the whole batch.
    /// </para>
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
    /// <b>Partitioned: one sum command for scalars, an individual read per container.</b> Resolved
    /// symbols are split by category. Scalars, strings and enums share a SINGLE ADS sum command —
    /// one round-trip for that whole subset. Structs, function blocks and arrays are read and
    /// decoded individually so their full nested tree survives, which a bare sum command would
    /// flatten to an opaque value; decoding one costs a further read PER MEMBER (or per element).
    /// So the batch is not one round-trip: budget it as one round-trip for all scalars plus the
    /// full member/element cost of every container in the request. Duplicate paths are
    /// de-duplicated before any of this.
    /// </para>
    /// <para>
    /// <b>Type metadata.</b> Every successful result also carries the symbol's
    /// <see cref="AdsValueResult.TypeName"/> and <see cref="AdsValueResult.Category"/>, on both the
    /// scalar and the container path — the same metadata
    /// <see cref="ReadValueWithMetadataAsync"/> reports for a single symbol.
    /// </para>
    /// <para>
    /// <b>Symbol not found.</b> A symbol that cannot be resolved on the PLC is recorded as a
    /// per-symbol <see cref="AdsValueResult.Failure(Exception, string?)"/> carrying an <see cref="AdsErrorException"/>
    /// with <see cref="AdsErrorCode.DeviceSymbolNotFound"/> before either read path runs, and is
    /// excluded from both. Resolution happens exactly once per path.
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
    /// Calls a method on a PLC function block or program over ADS.
    /// </summary>
    /// <param name="symbolPath">The instance path of the function block, e.g. <c>MAIN.ErrorHandler</c>.</param>
    /// <param name="methodName">The method name, e.g. <c>AcknowledgeAlarm</c>.</param>
    /// <param name="parameters">Input arguments in declaration order.</param>
    /// <param name="ct">Cancels the call; the per-target timeout applies as elsewhere.</param>
    /// <remarks>
    /// The PLC method must carry <c>{attribute 'TcRpcEnable'}</c>; without it TwinCAT does not
    /// expose it over ADS and the call fails as an unknown method.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="symbolPath"/>, <paramref name="methodName"/> or
    /// <paramref name="parameters"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="AdsErrorException">
    /// The symbol was not found, or the call reported a non-success ADS error code.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The symbol resolved but is not RPC-callable — it is not a function block or program
    /// instance. The message names the path and the method.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    /// <exception cref="TimeoutException">Thrown when the per-target timeout elapses first.</exception>
    Task<AdsRpcResult> InvokeRpcMethodAsync(
        string symbolPath, string methodName, object?[] parameters, CancellationToken ct);

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

    /// <summary>Reads the target device's name and version.</summary>
    /// <param name="ct">Cancels the operation; <see cref="PlcTargetOptions.TimeoutMs"/> applies.</param>
    /// <returns>The device's <see cref="AdsDeviceInfo"/>.</returns>
    /// <exception cref="AdsErrorException">Thrown when the read reports a non-success error code.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    /// <exception cref="TimeoutException">Thrown when the per-target timeout elapses first.</exception>
    Task<AdsDeviceInfo> GetDeviceInfoAsync(CancellationToken ct);

    /// <summary>
    /// Issues an ADS WriteControl request, asking the device to transition to
    /// <paramref name="state"/>.
    /// </summary>
    /// <param name="state">The requested ADS state, for example <see cref="AdsState.Run"/> or <see cref="AdsState.Stop"/>.</param>
    /// <param name="deviceState">
    /// The device-specific state word accompanying the request. Pass <c>0</c> unless the target
    /// documents a meaning for it.
    /// </param>
    /// <param name="ct">Cancels the operation; <see cref="PlcTargetOptions.TimeoutMs"/> applies.</param>
    /// <exception cref="AdsErrorException">Thrown when the device rejects the transition.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    /// <exception cref="TimeoutException">Thrown when the per-target timeout elapses first.</exception>
    /// <remarks>
    /// The request is asynchronous at the protocol level: a completed call means the device
    /// ACCEPTED the request, not that it has finished transitioning. Poll
    /// <see cref="GetAdsStateAsync"/> to observe the settled state.
    /// </remarks>
    Task WriteControlAsync(AdsState state, ushort deviceState, CancellationToken ct);

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

    /// <summary>
    /// Subscribes to value-change notifications for <paramref name="symbolPath"/>, delivering the
    /// symbol path, value, PLC type name and PLC-reported timestamp on each change.
    /// </summary>
    /// <param name="symbolPath">The fully-qualified PLC symbol to watch.</param>
    /// <param name="cycleTimeMs">Minimum interval, in milliseconds, between notifications.</param>
    /// <param name="callback">Invoked on each notification.</param>
    /// <param name="ct">Cancels the initial registration, not the subscription itself.</param>
    /// <returns>A handle whose disposal removes the subscription permanently.</returns>
    /// <remarks>
    /// <para>
    /// <b>Durability is identical</b> to
    /// <see cref="SubscribeAsync(string,int,System.Action{string,object?},CancellationToken)"/>:
    /// subscriptions registered through the durable facade survive reconnects and are
    /// re-registered automatically (the returned <see cref="IDisposable"/> stays valid across a
    /// reconnect, and disposing it removes the subscription for good), and registration during an
    /// outage follows the same wait-then-throw contract. Callbacks likewise arrive on a background
    /// thread — never the caller's — and a throwing callback is logged at Warning without tearing
    /// down the subscription. Prefer this overload when the consumer needs to report the value's
    /// PLC type or the change's timestamp.
    /// </para>
    /// <para>
    /// <b>Threading is identical for scalar symbols only</b> — see the container paragraph below,
    /// which is the one place this overload's delivery behaviour deliberately differs from the
    /// untyped one.
    /// </para>
    /// <para>
    /// <b><see cref="AdsNotification.TypeName"/></b> is the symbol's own PLC type, resolved once
    /// when the subscription is registered — the same name a
    /// <see cref="ReadValueWithMetadataAsync"/> of that symbol reports. A simulated connection has
    /// no declared PLC types and infers it from the written value's runtime type instead.
    /// </para>
    /// <para>
    /// <b><see cref="AdsNotification.Timestamp"/></b> is the time the ADS notification itself
    /// carries — recorded by the PLC, not measured on arrival, so it is unaffected by delivery
    /// latency. A simulated connection has no PLC clock and reports the moment of the simulated
    /// write.
    /// </para>
    /// <para>
    /// <b>Most container symbols deliver slightly later.</b> Scalars, strings, enums AND opaque
    /// structs or function blocks (those exposing no sub-symbols) decode with no ADS I/O and fire
    /// directly from the notification thread. Decoding a struct or function block WITH sub-symbols,
    /// or an array, performs one ADS read per member/element, which cannot run on that thread, so
    /// for those the decode is moved onto the thread pool and the callback fires when it completes.
    /// Notifications for such a symbol may therefore arrive slightly later — and, under a burst
    /// faster than the decode, out of order.
    /// </para>
    /// <para>
    /// <b>For those symbols the value and the timestamp do not describe the same instant.</b>
    /// <see cref="AdsNotification.Timestamp"/> is the PLC's time for the change that triggered the
    /// notification, but the member/element reads that build the value run later, on the thread
    /// pool, and read whatever the PLC holds AT THAT MOMENT. Under a burst the pair is therefore
    /// incoherent: the value may reflect a change newer than the timestamp beside it, and two
    /// notifications can carry values from the same underlying state under two different
    /// timestamps. Treat a container notification as "this symbol changed at T, here is a recent
    /// value" — not as a snapshot taken at T. Consumers republishing the pair verbatim (an SSE or
    /// websocket feed, say) are passing that incoherence on to their own subscribers. A scalar has
    /// no such gap: its value is decoded from the notification's own payload, so value and
    /// timestamp genuinely describe the same instant.
    /// </para>
    /// <para>
    /// <b>Disposal versus an in-flight container decode.</b> The untyped overload promises a
    /// callback may fire concurrently with, but never after, disposal COMPLETES. That promise holds
    /// here too, and disposing the handle additionally ABORTS an in-flight container decode — its
    /// remaining member reads are cancelled and the notification is dropped rather than delivered
    /// to a subscriber that has already torn its sink down. Note the promise is, for both
    /// overloads, "never after disposal completes" and not "never concurrently with disposal":
    /// detaching from the notification source does not wait for a callback that is already running,
    /// so a subscriber whose sink cannot tolerate a concurrent late write must guard it itself.
    /// </para>
    /// </remarks>
    Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<AdsNotification> callback, CancellationToken ct);

    /// <summary>
    /// Enumerates the symbols directly under <paramref name="parentPath"/>, each with its ENTIRE
    /// nested subtree populated. Equivalent to
    /// <see cref="GetSymbolsAsync(string?, bool, CancellationToken)"/> with
    /// <c>includeChildren: true</c>.
    /// </summary>
    /// <param name="parentPath">
    /// The container to enumerate, or <see langword="null"/>/empty for the root symbols.
    /// </param>
    /// <param name="ct">Cancels the wait for the browse (see remarks) — it cannot interrupt the browse itself.</param>
    /// <returns>
    /// The immediate symbols under <paramref name="parentPath"/>, each carrying its full recursive
    /// subtree in <see cref="AdsSymbolInfo.Children"/>.
    /// </returns>
    /// <exception cref="AdsErrorException">
    /// Thrown with <see cref="AdsErrorCode.DeviceSymbolNotFound"/> when
    /// <paramref name="parentPath"/> does not resolve.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    /// <exception cref="TimeoutException">
    /// Thrown when <see cref="PlcTargetOptions.SymbolBrowseTimeoutMs"/> elapses first — note this
    /// is the browse timeout, NOT <see cref="PlcTargetOptions.TimeoutMs"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>This is NOT a one-level browse.</b> Children are populated recursively all the way down,
    /// so a call with a <see langword="null"/> parent projects EVERY symbol on the PLC into
    /// <see cref="AdsSymbolInfo"/> objects — on a large program, tens of thousands of them. For
    /// interactive drill-down, pass <c>includeChildren: false</c> to
    /// <see cref="GetSymbolsAsync(string?, bool, CancellationToken)"/> and call again per level;
    /// that is the cheap shape this overload is not.
    /// </para>
    /// <para>
    /// Browsing uploads the PLC's symbol table, a blocking call with no cancellable overload. The
    /// implementation runs it on the thread pool and races it against
    /// <paramref name="ct"/>/<see cref="PlcTargetOptions.SymbolBrowseTimeoutMs"/> so the CALLER
    /// stops waiting either way; a browse that loses that race is abandoned — it keeps running to
    /// completion on its thread-pool thread, but its result is discarded. The browse itself is
    /// never interrupted, only the caller's wait for it. Note the abandoned browse keeps
    /// allocating the projection above on a thread-pool thread after the caller has given up.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, CancellationToken ct);

    /// <summary>
    /// Enumerates the symbols directly under <paramref name="parentPath"/>, choosing whether each
    /// one carries its nested subtree.
    /// </summary>
    /// <param name="parentPath">
    /// The container to enumerate, or <see langword="null"/>/empty for the root symbols.
    /// </param>
    /// <param name="includeChildren">
    /// When <see langword="true"/> each returned symbol carries its FULL recursive subtree in
    /// <see cref="AdsSymbolInfo.Children"/>. When <see langword="false"/> every returned symbol has
    /// a <see langword="null"/> <see cref="AdsSymbolInfo.Children"/> — one level only, which is what
    /// interactive drill-down wants and what keeps a root browse from projecting the whole PLC.
    /// The flag has the same meaning as on <see cref="SearchSymbolsAsync"/>.
    /// </param>
    /// <param name="ct">Cancels the wait for the browse — it cannot interrupt the browse itself.</param>
    /// <returns>The immediate symbols under <paramref name="parentPath"/>.</returns>
    /// <exception cref="AdsErrorException">
    /// Thrown with <see cref="AdsErrorCode.DeviceSymbolNotFound"/> when
    /// <paramref name="parentPath"/> does not resolve.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    /// <exception cref="TimeoutException">
    /// Thrown when <see cref="PlcTargetOptions.SymbolBrowseTimeoutMs"/> elapses first.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b><paramref name="includeChildren"/> bounds the projection, not the upload.</b> The symbol
    /// table upload is the same either way — it is what
    /// <see cref="PlcTargetOptions.SymbolBrowseTimeoutMs"/> exists to bound. What the flag controls
    /// is how much of the resulting tree is walked and turned into
    /// <see cref="AdsSymbolInfo"/> objects, which for a root browse of a large program is the
    /// difference between one level and every symbol on the PLC.
    /// </para>
    /// <para>
    /// Same thread-pool/timeout-race and abandon-on-timeout semantics as
    /// <see cref="GetSymbolsAsync(string?, CancellationToken)"/>; see its remarks.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, bool includeChildren, CancellationToken ct);

    /// <summary>
    /// Searches the whole symbol tree for symbols whose instance path contains
    /// <paramref name="pattern"/>, compared case-insensitively.
    /// </summary>
    /// <param name="pattern">Substring to match against each symbol's instance path.</param>
    /// <param name="includeChildren">
    /// When <see langword="true"/> each match carries its nested children; when
    /// <see langword="false"/> every match has a <see langword="null"/>
    /// <see cref="AdsSymbolInfo.Children"/>, which keeps large result sets flat and cheap.
    /// </param>
    /// <param name="ct">Cancels the wait for the search (see remarks) — it cannot interrupt the search itself.</param>
    /// <returns>All matching symbols, or an empty list when nothing matches.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    /// <exception cref="TimeoutException">
    /// Thrown when <see cref="PlcTargetOptions.SymbolBrowseTimeoutMs"/> elapses first.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This walks the ENTIRE symbol tree, so its cost is proportional to the PLC's total symbol
    /// count — prefer
    /// <see cref="GetSymbolsAsync(string?, bool, CancellationToken)"/> with
    /// <c>includeChildren: false</c> for interactive drill-down of one level at a time.
    /// </para>
    /// <para>
    /// Same thread-pool/timeout-race semantics as
    /// <see cref="GetSymbolsAsync(string?, CancellationToken)"/>: the underlying
    /// walk cannot itself be interrupted, only the caller's wait for it (see its remarks).
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<AdsSymbolInfo>> SearchSymbolsAsync(string pattern, bool includeChildren, CancellationToken ct);
}
