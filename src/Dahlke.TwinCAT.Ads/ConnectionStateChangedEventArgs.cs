namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Describes a single transition of a pooled PLC connection between
/// <see cref="ConnectionState"/> values.
/// </summary>
/// <remarks>
/// <para>
/// Both <see cref="State"/> and <see cref="PreviousState"/> are reported so that
/// subscribers can detect outage gaps — for example, observing a transition
/// <em>into</em> <see cref="ConnectionState.Disconnected"/> from
/// <see cref="ConnectionState.Connected"/> marks the start of an outage, while
/// the next transition back to <see cref="ConnectionState.Connected"/> closes it.
/// </para>
/// <para>
/// <strong>Threading:</strong> instances are raised from the pool's background
/// reconnect loop thread, not the thread that started the pool. Handlers must be
/// thread-safe and should not block; any exception thrown by a handler is caught
/// and logged by the pool and will not interrupt reconnection.
/// </para>
/// </remarks>
public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initialises a new instance of the
    /// <see cref="ConnectionStateChangedEventArgs"/> class.
    /// </summary>
    /// <param name="plcId">The configured identifier of the affected PLC target.</param>
    /// <param name="state">The state the connection has transitioned to.</param>
    /// <param name="previousState">The state the connection transitioned from.</param>
    public ConnectionStateChangedEventArgs(
        string plcId,
        ConnectionState state,
        ConnectionState previousState)
    {
        PlcId = plcId;
        State = state;
        PreviousState = previousState;
    }

    /// <summary>
    /// The configured identifier of the PLC target whose state changed.
    /// </summary>
    public string PlcId { get; }

    /// <summary>
    /// The state the connection has transitioned to.
    /// </summary>
    public ConnectionState State { get; }

    /// <summary>
    /// The state the connection transitioned from.
    /// </summary>
    public ConnectionState PreviousState { get; }
}
