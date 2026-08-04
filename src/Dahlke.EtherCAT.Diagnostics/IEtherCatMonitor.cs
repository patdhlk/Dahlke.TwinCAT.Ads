namespace Dahlke.EtherCAT.Diagnostics;

/// <summary>
/// The EtherCAT polling monitor. Runs as a hosted service; this interface exposes only the one
/// operation a consumer needs to call into it.
/// </summary>
public interface IEtherCatMonitor
{
    /// <summary>
    /// Re-arms CRC threshold notifications for one slave, so a subsequent breach notifies again.
    /// Call after resetting that slave's error counters — otherwise the monitor still considers the
    /// slave notified and stays silent.
    /// </summary>
    void ClearCrcNotification(int masterId, ushort address);
}
