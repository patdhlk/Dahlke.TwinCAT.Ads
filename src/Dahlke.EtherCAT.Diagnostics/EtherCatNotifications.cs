namespace Dahlke.EtherCAT.Diagnostics;

/// <summary>
/// Emitted when a master's current or requested EtherCAT state changes between two known-good
/// poll cycles.
/// </summary>
public sealed class MasterStateChangedEvent : IEtherCatEvent
{
    /// <inheritdoc/>
    public required int MasterId { get; init; }

    /// <summary>The master's display name.</summary>
    public required string MasterName { get; init; }

    /// <summary>The master's new state.</summary>
    public required string CurrentState { get; init; }

    /// <summary>The master's state on the prior known-good cycle.</summary>
    public required string PreviousState { get; init; }

    /// <summary>The state the master has been asked to transition to, as of the new reading.</summary>
    public required string RequestedState { get; init; }
}

/// <summary>
/// Emitted when adsify's view of a master's diagnostics goes unreadable, and again when it comes
/// back. This is a statement about the READ, not about the bus: <c>degraded: true</c> says adsify
/// could not read the master, not that the master reported a fault.
///
/// <para>
/// Emitted on TRANSITION only — once when a cycle first fails and once when a cycle first
/// succeeds again — so a master polled every second while unreachable produces two events in
/// total, not one per second. While degraded, no state or presence events are emitted for that
/// master at all, because adsify has nothing it observed to report; the REST snapshot keeps
/// serving the last known-good reading with <c>diagnosticsDegraded: true</c>.
/// </para>
/// </summary>
public sealed class MasterDiagnosticsDegradedEvent : IEtherCatEvent
{
    /// <inheritdoc/>
    public required int MasterId { get; init; }

    /// <summary>The master's display name.</summary>
    public required string MasterName { get; init; }

    /// <summary>True on entering the degraded state, false on recovering from it.</summary>
    public required bool Degraded { get; init; }

    /// <summary>Which read failed. Null on the recovery event.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Emitted when a slave's current EtherCAT state or error flag changes between two known-good
/// poll cycles.
/// </summary>
public sealed class SlaveStateChangedEvent : IEtherCatEvent
{
    /// <inheritdoc/>
    public required int MasterId { get; init; }

    /// <summary>The slave's fixed address on the bus.</summary>
    public required ushort Address { get; init; }

    /// <summary>The slave's display name.</summary>
    public required string Name { get; init; }

    /// <summary>The slave's new state.</summary>
    public required string CurrentState { get; init; }

    /// <summary>The slave's state on the prior known-good cycle.</summary>
    public required string PreviousState { get; init; }

    /// <summary>Whether the slave now reports the error flag.</summary>
    public required bool HasError { get; init; }
}

/// <summary>
/// Emitted when a slave known from a prior poll cycle appears or disappears. A slave's first
/// appearance, with no prior cycle to compare against, does not emit this event.
/// </summary>
public sealed class SlavePresenceChangedEvent : IEtherCatEvent
{
    /// <inheritdoc/>
    public required int MasterId { get; init; }

    /// <summary>The slave's fixed address on the bus.</summary>
    public required ushort Address { get; init; }

    /// <summary>The slave's display name.</summary>
    public required string Name { get; init; }

    /// <summary>True when the slave has just appeared; false when it has just disappeared.</summary>
    public required bool IsPresent { get; init; }
}

/// <summary>
/// Emitted once when a port's CRC error count first reaches <see cref="Threshold"/>, and not
/// again until <see cref="IEtherCatMonitor.ClearCrcNotification"/> re-arms it for that port.
/// </summary>
public sealed class CrcErrorThresholdExceededEvent : IEtherCatEvent
{
    /// <inheritdoc/>
    public required int MasterId { get; init; }

    /// <summary>The slave's fixed address on the bus.</summary>
    public required ushort Address { get; init; }

    /// <summary>The slave's display name.</summary>
    public required string Name { get; init; }

    /// <summary>Which port — <c>"A"</c>, <c>"B"</c>, <c>"C"</c> or <c>"D"</c> — exceeded the threshold.</summary>
    public required string Port { get; init; }

    /// <summary>The port's CRC error count at the moment the threshold was crossed.</summary>
    public required int CrcCount { get; init; }

    /// <summary>The configured <see cref="EtherCatOptions.CrcErrorThreshold"/> that was exceeded.</summary>
    public required int Threshold { get; init; }
}

/// <summary>
/// Emitted when a sync unit's error flag changes between two known-good poll cycles. Currently
/// unreachable — <see cref="IEtherCatClient.GetSyncUnitsAsync"/> always returns an empty list, so
/// no sync unit ever appears in two consecutive snapshots to compare.
/// </summary>
public sealed class SyncUnitFaultEvent : IEtherCatEvent
{
    /// <inheritdoc/>
    public required int MasterId { get; init; }

    /// <summary>The sync unit's identifier.</summary>
    public required int SyncUnitId { get; init; }

    /// <summary>The sync unit's new error state.</summary>
    public required bool HasError { get; init; }

    /// <summary>The sync unit's fault count as of the new reading.</summary>
    public required long FaultCounter { get; init; }
}
