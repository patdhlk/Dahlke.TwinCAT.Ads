namespace Dahlke.EtherCAT.Diagnostics;

/// <summary>
/// Receives diagnostics events from the monitor. Implement this to deliver them over whatever
/// transport the application uses; the library deliberately knows nothing about transports.
/// </summary>
/// <remarks>
/// The monitor gates delivery on <see cref="EtherCatOptions.EnableNotifications"/> and catches
/// everything this method throws, logging at Warning. A handler that fails therefore loses its
/// events but cannot stop the poll loop — the resilience is the library's responsibility, not the
/// implementer's.
/// </remarks>
public interface IEtherCatDiagnosticsHandler
{
    /// <summary>Delivers one event for one PLC.</summary>
    Task HandleAsync(string plcId, IEtherCatEvent evt, CancellationToken ct);
}
