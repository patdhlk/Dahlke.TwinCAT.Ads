using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Receives one raw device notification payload.
/// </summary>
/// <param name="data">
/// The notification's bytes, valid ONLY for the duration of this call.
/// </param>
/// <remarks>
/// <para>
/// <b>Why a <see cref="ReadOnlySpan{T}"/> and not <see cref="ReadOnlyMemory{T}"/>.</b>
/// The underlying buffer belongs to the transport and is reused after the handler
/// returns. A <see cref="ReadOnlyMemory{T}"/> could be captured in a lambda or
/// stashed in a field, and reading it later would yield silently wrong bytes — a
/// bug with no exception and no stack trace. A span cannot be captured or stored,
/// so the compiler rejects that mistake outright. A handler that needs to keep the
/// data writes an explicit <c>data.ToArray()</c>, which is visible at the call
/// site.
/// </para>
/// <para>
/// The trade-off is that a handler cannot be <c>async</c>. Raw byte fan-out does
/// not need it; do the work by copying out and posting to your own queue.
/// </para>
/// </remarks>
public delegate void RawNotificationHandler(ReadOnlySpan<byte> data);

/// <summary>
/// A low-level ADS channel addressing one <c>(amsNetId, port)</c> pair by index
/// group and index offset, for targets the symbol API cannot reach — EtherCAT
/// masters and slaves, and the TwinCAT system service.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not <see cref="IDisposable"/>, by design.</b> Obtain one from
/// <see cref="IAdsRawChannelFactory.Get"/> and hold it as long as you like: its
/// identity is stable for the factory's lifetime and it owns nothing the caller
/// must release. The underlying transport is created lazily, dropped when the
/// channel goes idle with no live subscription, and re-created on the next
/// operation — all invisibly. This mirrors
/// <see cref="IAdsConnectionPool.GetConnection"/>, whose facades are likewise
/// never disposed by consumers.
/// </para>
/// <para>
/// <b>A subscription handle IS the caller's to release.</b> The channel needs no
/// disposal, but the handle returned by
/// <see cref="SubscribeAsync"/> does: until it is disposed the subscription is
/// live, and a live subscription deliberately holds the transport open against
/// idle eviction. Dropping the handle on the floor leaks a connection for the
/// factory's lifetime.
/// </para>
/// <para>
/// <b>Host shutdown is the one point where that invisibility stops.</b> Once the
/// factory has been stopped or disposed the channel object remains perfectly
/// usable, but the transport is NOT re-created: every operation throws
/// <see cref="AdsConnectionUnavailableException"/> immediately rather than
/// opening a connection nothing would ever release. This matters for a consumer
/// hosted service that stops after the factory does.
/// </para>
/// <para>
/// <b>Thread safety.</b> All members are safe for concurrent use from any thread.
/// </para>
/// <para>
/// <b>Timeouts bound each ATTEMPT, not the retry sequence.</b> With the default
/// <see cref="AdsRawChannelOptions.RetryCount"/> of 1, a call using the default
/// 5000 ms bound can take up to 10 seconds before throwing
/// <see cref="TimeoutException"/>. The worst case is
/// <c>TimeoutMs × (RetryCount + 1)</c>. Caller cancellation is exempt: a cancelled
/// token aborts immediately and no further attempt is made. This applies to the
/// read, write, read-write and state operations; <see cref="SubscribeAsync"/> is
/// bounded only by its cancellation token, as its remarks explain.
/// </para>
/// <para>
/// <b>Retry applies only to a timeout with no device answer</b>, and re-creates
/// the transport before reissuing. A device that answers with an ADS error code
/// is never retried — that is an answer, not a failure. In particular
/// <see cref="AdsErrorCode.PortNotConnected"/>,
/// <see cref="AdsErrorCode.TargetPortNotFound"/> and
/// <see cref="AdsErrorCode.DeviceTimeOut"/> are the normal replies from an
/// EtherCAT slave with no mailbox and never tear the channel down.
/// </para>
/// </remarks>
public interface IAdsRawChannel
{
    /// <summary>The target AMS Net ID this channel addresses.</summary>
    string AmsNetId { get; }

    /// <summary>The target AMS port this channel addresses.</summary>
    int Port { get; }

    /// <summary>
    /// The channel's current connection state.
    /// </summary>
    /// <remarks>
    /// Observational only, like <see cref="IAdsConnection.State"/>. Reports
    /// <see cref="ConnectionState.Disconnected"/> until the first operation or
    /// subscription creates the underlying transport, so a channel that has never
    /// been used is
    /// indistinguishable from one whose target is unreachable. It is a hint, not
    /// a guard: the operation methods never consult it.
    /// </remarks>
    ConnectionState State { get; }

    /// <summary>
    /// Reads into <paramref name="destination"/> using the configured
    /// <see cref="AdsRawChannelOptions.TimeoutMs"/>.
    /// </summary>
    /// <param name="indexGroup">Index group to read from.</param>
    /// <param name="indexOffset">Index offset to read from.</param>
    /// <param name="destination">Buffer the response is copied into.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The number of bytes actually read, which may be fewer than
    /// <paramref name="destination"/>'s length.</returns>
    /// <exception cref="AdsErrorException">The device answered with an error code.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="TimeoutException">Every attempt exceeded its bound.</exception>
    /// <exception cref="AdsConnectionUnavailableException">The transport could not be opened.</exception>
    Task<int> ReadAsync(uint indexGroup, uint indexOffset, Memory<byte> destination, CancellationToken ct);

    /// <inheritdoc cref="ReadAsync(uint, uint, Memory{byte}, CancellationToken)"/>
    /// <param name="indexGroup">Index group to read from.</param>
    /// <param name="indexOffset">Index offset to read from.</param>
    /// <param name="destination">Buffer the response is copied into.</param>
    /// <param name="timeout">Overrides <see cref="AdsRawChannelOptions.TimeoutMs"/> for THIS ATTEMPT.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<int> ReadAsync(uint indexGroup, uint indexOffset, Memory<byte> destination, TimeSpan timeout, CancellationToken ct);

    /// <summary>Writes <paramref name="source"/> using the configured timeout.</summary>
    /// <param name="indexGroup">Index group to write to.</param>
    /// <param name="indexOffset">Index offset to write to.</param>
    /// <param name="source">Payload to write.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <exception cref="AdsErrorException">The device answered with an error code.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="TimeoutException">Every attempt exceeded its bound.</exception>
    Task WriteAsync(uint indexGroup, uint indexOffset, ReadOnlyMemory<byte> source, CancellationToken ct);

    /// <inheritdoc cref="WriteAsync(uint, uint, ReadOnlyMemory{byte}, CancellationToken)"/>
    /// <param name="indexGroup">Index group to write to.</param>
    /// <param name="indexOffset">Index offset to write to.</param>
    /// <param name="source">Payload to write.</param>
    /// <param name="timeout">Overrides the configured timeout for THIS ATTEMPT.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task WriteAsync(uint indexGroup, uint indexOffset, ReadOnlyMemory<byte> source, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="source"/> and reads the response in ONE round trip
    /// — the ADS ReadWrite service.
    /// </summary>
    /// <remarks>
    /// This is how the TwinCAT file protocol works: <c>FILE_OPEN</c> sends the
    /// path as write data and returns the handle as read data. A read/write-only
    /// surface cannot express it.
    /// </remarks>
    /// <param name="indexGroup">Index group of the service.</param>
    /// <param name="indexOffset">Index offset of the service.</param>
    /// <param name="destination">Buffer the response is copied into.</param>
    /// <param name="source">Payload sent with the request.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The number of bytes actually read.</returns>
    Task<int> ReadWriteAsync(
        uint indexGroup, uint indexOffset,
        Memory<byte> destination, ReadOnlyMemory<byte> source, CancellationToken ct);

    /// <inheritdoc cref="ReadWriteAsync(uint, uint, Memory{byte}, ReadOnlyMemory{byte}, CancellationToken)"/>
    /// <param name="indexGroup">Index group of the service.</param>
    /// <param name="indexOffset">Index offset of the service.</param>
    /// <param name="destination">Buffer the response is copied into.</param>
    /// <param name="source">Payload sent with the request.</param>
    /// <param name="timeout">Overrides the configured timeout for THIS ATTEMPT.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<int> ReadWriteAsync(
        uint indexGroup, uint indexOffset,
        Memory<byte> destination, ReadOnlyMemory<byte> source, TimeSpan timeout, CancellationToken ct);

    /// <summary>Reads the target's ADS and device state.</summary>
    /// <remarks>
    /// Returns Beckhoff's <see cref="StateInfo"/> — the full
    /// <see cref="AdsState"/>/<c>DeviceState</c> pair, not the bare enum
    /// <see cref="IAdsConnection.GetAdsStateAsync"/> returns. At this level the
    /// device state word is meaningful.
    /// </remarks>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The target's ADS state and device state word.</returns>
    Task<StateInfo> ReadStateAsync(CancellationToken ct);

    /// <summary>
    /// Subscribes to device notifications for the given index group and offset.
    /// </summary>
    /// <param name="indexGroup">The index group to watch.</param>
    /// <param name="indexOffset">The index offset to watch.</param>
    /// <param name="length">Payload length in bytes to request per notification.</param>
    /// <param name="cycleTimeMs">Minimum interval between notifications.</param>
    /// <param name="handler">Invoked on each notification.</param>
    /// <param name="ct">Cancels the initial registration, not the subscription itself.</param>
    /// <returns>A handle whose disposal removes the subscription permanently.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <b>Durable across transport drops.</b> The subscription is owned by this
    /// channel, not by the transport it is first registered on. When an operation
    /// times out and the channel rebuilds its transport, every live subscription
    /// is re-registered against the new one — exactly once — and the returned
    /// handle stays valid throughout. This mirrors
    /// <see cref="IAdsConnection.SubscribeAsync(string, int, System.Action{string, object?}, CancellationToken)"/>,
    /// whose subscriptions survive a pool reconnect the same way. A
    /// re-registration that ITSELF fails is logged at Warning and the subscription
    /// is retained for the next rebuild rather than discarded — but it delivers
    /// nothing until one happens.
    /// </para>
    /// <para>
    /// <b>A live subscription pins the channel against idle eviction.</b> The
    /// sweeper will not release a transport that is serving notifications.
    /// </para>
    /// <para>
    /// <b>Neither the timeout nor the retry policy applies here.</b> Unlike the
    /// read and write operations, the initial registration — and every
    /// re-registration after a drop — is bounded only by a cancellation token, not
    /// by <see cref="AdsRawChannelOptions.TimeoutMs"/>, and a failed registration
    /// is not retried on a fresh transport. Pass a <paramref name="ct"/> carrying a
    /// deadline if you need one.
    /// </para>
    /// <para>
    /// <b>Threading.</b> <paramref name="handler"/> is invoked on the transport's
    /// notification thread: against a real target the ADS notification thread —
    /// never the caller's, and never the thread that awaited this method. A
    /// SIMULATED channel has no such thread and fires the handler inline, on
    /// whichever thread performed the write. Handlers must therefore be
    /// thread-safe and must not block. An exception thrown by a handler is caught
    /// and logged at Warning and does NOT tear down the subscription.
    /// </para>
    /// <para>
    /// <b>Disposal</b> is idempotent and thread-safe. As with symbol
    /// subscriptions, the promise is that a handler never fires after disposal
    /// COMPLETES — not that it never fires concurrently WITH disposal. A sink that
    /// cannot tolerate a concurrent late write must guard itself. Disposal does not
    /// WAIT for the removal round trip to the device: the channel stops delivering
    /// the moment the handle is disposed, whether or not that round trip succeeds.
    /// </para>
    /// <para>
    /// <b>Host shutdown ends delivery silently.</b> Once the factory has stopped,
    /// no transport is rebuilt, so a live subscription simply goes quiet: the
    /// handler stops being called and nothing is raised to it. Disposing the handle
    /// afterwards remains safe.
    /// </para>
    /// <para>
    /// <b>Simulated channels ignore <paramref name="cycleTimeMs"/></b> and fire on
    /// every write made THROUGH the channel to the watched slot, with no
    /// coalescing. Seeding via <see cref="ISimulatedRawChannel.Seed"/> writes the
    /// slot without firing — it arranges state rather than reporting a change.
    /// </para>
    /// </remarks>
    Task<IDisposable> SubscribeAsync(
        uint indexGroup, uint indexOffset, int length,
        int cycleTimeMs, RawNotificationHandler handler, CancellationToken ct);
}
