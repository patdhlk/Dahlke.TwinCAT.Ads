using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

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
/// channel goes idle, and re-created on the next operation — all invisibly. This
/// mirrors <see cref="IAdsConnectionPool.GetConnection"/>, whose facades are
/// likewise never disposed by consumers.
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
/// token aborts immediately and no further attempt is made.
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
    /// <see cref="ConnectionState.Disconnected"/> until the first operation
    /// creates the underlying transport, so a channel that has never been used is
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
}
