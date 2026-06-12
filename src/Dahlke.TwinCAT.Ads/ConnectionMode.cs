namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Selects how a PLC target establishes its connection.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Real"/> connects to a physical (or virtual) PLC over ADS/AMS.
/// <see cref="Simulated"/> backs the target with an in-memory value store for
/// offline development and testing — no router, no AMS Net ID, no hardware.
/// </para>
/// <para>
/// This is modelled as an enum rather than a boolean to leave room for future
/// connection modes (e.g. a recording/replay mode) without a breaking change to
/// the configuration surface.
/// </para>
/// </remarks>
public enum ConnectionMode
{
    /// <summary>
    /// Connect to a live PLC over ADS/AMS. Requires a valid AMS Net ID.
    /// </summary>
    Real,

    /// <summary>
    /// Back the target with an in-memory store. Reads return previously written
    /// (or seeded) values; no AMS/ADS connection is established.
    /// </summary>
    Simulated,
}
