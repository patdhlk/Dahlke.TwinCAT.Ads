namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Thrown when an operation on a PLC target cannot be served because the target
/// has no live underlying connection and none became available within the wait
/// window — the target has either never connected, is mid-outage awaiting
/// reconnection, or the connection pool has been stopped.
/// </summary>
/// <remarks>
/// <para>
/// The per-target <see cref="IAdsConnection"/> handed out by the connection pool
/// is a stable facade: its identity never changes for the pool's lifetime, even
/// as the underlying managed connection is rebuilt on every recovery. When a
/// caller invokes an operation while the facade has no live connection to route
/// to, the operation does not fail immediately: it waits up to the target's
/// <see cref="PlcTargetOptions.TimeoutMs"/> for a reconnection to be published.
/// This exception is thrown only after that wait window elapses without a
/// connection arriving (or immediately if the pool has been stopped). A reconnect
/// landing inside the window lets the call proceed instead of throwing.
/// </para>
/// <para>
/// This is an observational, transient failure: the same facade may succeed on a
/// later call once the pool reconnects. Callers that wish to distinguish a
/// disconnected target from other ADS errors can catch this type specifically.
/// A caller-supplied <see cref="System.Threading.CancellationToken"/> that fires
/// during the wait surfaces as an <see cref="OperationCanceledException"/>, not
/// this exception.
/// </para>
/// </remarks>
public sealed class AdsConnectionUnavailableException : Exception
{
    /// <summary>
    /// The identifier of the PLC target whose connection was unavailable, or
    /// <see langword="null"/> if no target was associated with the failure.
    /// </summary>
    public string? PlcId { get; }

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="AdsConnectionUnavailableException"/> class for the given target,
    /// with a default message describing the disconnected/reconnecting state.
    /// </summary>
    /// <param name="plcId">The identifier of the unavailable PLC target.</param>
    public AdsConnectionUnavailableException(string plcId)
        : base(BuildMessage(plcId))
    {
        PlcId = plcId;
    }

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="AdsConnectionUnavailableException"/> class for the given target,
    /// wrapping an inner exception.
    /// </summary>
    /// <param name="plcId">The identifier of the unavailable PLC target.</param>
    /// <param name="innerException">The underlying cause of this failure.</param>
    public AdsConnectionUnavailableException(string plcId, Exception innerException)
        : base(BuildMessage(plcId), innerException)
    {
        PlcId = plcId;
    }

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="AdsConnectionUnavailableException"/> class with an explicit
    /// message.
    /// </summary>
    /// <param name="plcId">The identifier of the unavailable PLC target.</param>
    /// <param name="message">The message describing the failure.</param>
    /// <param name="innerException">
    /// The underlying cause of this failure, or <see langword="null"/>.
    /// </param>
    public AdsConnectionUnavailableException(string plcId, string message, Exception? innerException)
        : base(message, innerException)
    {
        PlcId = plcId;
    }

    private static string BuildMessage(string plcId)
        => $"PLC target '{plcId}' has no live connection — it is disconnected or reconnecting. " +
           "The operation cannot be served until the pool reconnects to the target.";
}
