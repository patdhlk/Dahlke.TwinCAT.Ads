namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// The lifecycle state of a pooled PLC connection, as observed by the
/// connection pool's reconnect loop.
/// </summary>
/// <remarks>
/// <para>
/// State transitions are surfaced through
/// <see cref="ConnectionStateChangedEventArgs"/>. A single reconnect attempt
/// walks <see cref="Disconnected"/> → <see cref="Connecting"/> → either
/// <see cref="Connected"/> (success) or back to <see cref="Disconnected"/>
/// (failure), so a persistently failing target legitimately oscillates between
/// <see cref="Connecting"/> and <see cref="Disconnected"/> on every attempt —
/// this is the expected retry signal, not event spam.
/// </para>
/// <para>
/// <see cref="Disconnected"/> intentionally covers two situations that callers
/// do not need to distinguish: a connection that was lost and is pending
/// reconnection, and a target that has never connected. Subscribers interested
/// in outage gaps observe the entry into <see cref="Disconnected"/> in both
/// cases.
/// </para>
/// </remarks>
public enum ConnectionState
{
    /// <summary>
    /// The connection is not available. This covers both a connection that was
    /// lost and is awaiting reconnection, and a target that has never connected.
    /// This is the default (zero) value, so an unobserved or freshly created
    /// target reads as <see cref="Disconnected"/>.
    /// </summary>
    Disconnected = 0,

    /// <summary>
    /// A connection attempt is in progress: the loop has begun building and
    /// connecting the underlying ADS connection but it is not yet usable.
    /// </summary>
    Connecting,

    /// <summary>
    /// The connection is established and currently passing health checks.
    /// </summary>
    Connected,
}
