namespace Dahlke.EtherCAT.Diagnostics;

/// <summary>Per-PLC EtherCAT diagnostics polling configuration.</summary>
public sealed class EtherCatOptions
{
    /// <summary>How often the monitor polls this PLC's masters, in milliseconds.</summary>
    public int PollingIntervalMs { get; set; } = 1000;

    /// <summary>
    /// CRC error count on one port that triggers a <see cref="CrcErrorThresholdExceededEvent"/>.
    /// Notified once per port until <see cref="IEtherCatMonitor.ClearCrcNotification"/> re-arms
    /// it, however far above the threshold the count climbs.
    /// </summary>
    public int CrcErrorThreshold { get; set; } = 100;

    /// <summary>
    /// Whether the monitor emits diagnostics events for this PLC. State-change detection and the
    /// snapshot cache run regardless — this only gates delivery to
    /// <see cref="IEtherCatDiagnosticsHandler"/>.
    /// </summary>
    public bool EnableNotifications { get; set; } = true;

    /// <summary>
    /// Wall-clock bound on ONE master's poll cycle. A cycle that exceeds it is abandoned: the last
    /// known-good snapshot is kept and the master is marked degraded, rather than the cycle running
    /// on and freezing the snapshot behind it.
    ///
    /// <para>
    /// An absolute value rather than a multiple of <see cref="PollingIntervalMs"/>, because the
    /// question an operator has is "how long can this reading silently freeze", not "how many
    /// intervals". A healthy 8-slave rack costs roughly 25 reads at single-digit milliseconds, so
    /// 5 s is about two orders of magnitude of headroom; a rack wide enough to need more raises
    /// this, and the overrun Warning names the budget so they know which knob to turn.
    /// </para>
    /// </summary>
    public int PollCycleBudgetMs { get; set; } = 5000;

    /// <summary>
    /// ADS timeout for a single CoE SDO read. Deliberately shorter than the client's own 10 s
    /// default for a diagnostic read: a slave without a mailbox cannot answer at all and the
    /// request runs to full timeout, so this is the latency a caller pays for asking a plain I/O
    /// terminal for a CoE object. A slave that does answer replies in tens of milliseconds.
    /// </summary>
    public int CoeTimeoutMs { get; set; } = 3000;

    /// <summary>Largest CoE object body accepted from a slave, in bytes.</summary>
    public int CoeMaxObjectBytes { get; set; } = 1024;
}
