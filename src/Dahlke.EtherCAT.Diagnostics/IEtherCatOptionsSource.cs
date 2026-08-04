namespace Dahlke.EtherCAT.Diagnostics;

/// <summary>
/// Supplies per-PLC EtherCAT options. Implement this over whatever shape the application already
/// stores configuration in, so adopting this library does not force a configuration migration.
/// </summary>
public interface IEtherCatOptionsSource
{
    /// <summary>
    /// Options for one PLC, or <see langword="null"/> when this PLC has no EtherCAT configuration —
    /// in which case the monitor skips it entirely rather than polling it with defaults.
    /// </summary>
    EtherCatOptions? For(string plcId);
}
