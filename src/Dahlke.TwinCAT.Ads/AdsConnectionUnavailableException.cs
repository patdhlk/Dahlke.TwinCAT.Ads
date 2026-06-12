namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Thrown when an operation is requested on a PLC target that currently has no
/// live underlying connection — the target has either never connected, is mid
/// outage awaiting reconnection, or the connection pool has been stopped.
/// </summary>
/// <remarks>
/// <para>
/// The per-target <see cref="IAdsConnection"/> handed out by the connection pool
/// is a stable facade: its identity never changes for the pool's lifetime, even
/// as the underlying managed connection is rebuilt on every recovery. When a
/// caller invokes an operation while the facade has no live connection to route
/// to, the operation fails fast with this exception rather than blocking or
/// silently returning a default.
/// </para>
/// <para>
/// This is an observational, transient failure: the same facade may succeed on a
/// later call once the pool reconnects. Callers that wish to distinguish a
/// disconnected target from other ADS errors can catch this type specifically.
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
