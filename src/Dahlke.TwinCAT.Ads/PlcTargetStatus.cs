namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// A point-in-time snapshot of one configured PLC target's connection status,
/// as reported by <see cref="IAdsConnectionPool.GetTargetStates"/>.
/// </summary>
/// <param name="PlcId">The configured identifier of the target.</param>
/// <param name="Mode">The target's configured <see cref="ConnectionMode"/>.</param>
/// <param name="State">
/// The target's <see cref="ConnectionState"/> at the moment of the snapshot.
/// <see cref="ConnectionState.Connected"/> means the link has been proven by a
/// real ADS round trip — not merely that a local socket association exists.
/// </param>
public sealed record PlcTargetStatus(string PlcId, ConnectionMode Mode, ConnectionState State);
