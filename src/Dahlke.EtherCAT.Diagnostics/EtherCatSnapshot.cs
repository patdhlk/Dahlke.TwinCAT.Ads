namespace Dahlke.EtherCAT.Diagnostics;

/// <summary>
/// One master's EtherCAT diagnostics as of one successful poll cycle — the last known-good
/// reading. See <see cref="IEtherCatCache"/>.
/// </summary>
public sealed class EtherCatSnapshot
{
    /// <summary>Which configured PLC this snapshot belongs to.</summary>
    public required string PlcId { get; init; }

    /// <summary>The master's own AMS NetId (port 0xFFFF) — distinct from the PLC's own NetId.</summary>
    public required string MasterAmsNetId { get; init; }

    /// <summary>The master's <see cref="EtherCatMasterInfo.DeviceId"/> this snapshot was polled from.</summary>
    public required int MasterDeviceId { get; init; }

    /// <summary>The master's display name.</summary>
    public required string MasterName { get; init; }

    /// <summary>When this cycle's reading completed, in UTC.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The master's state and configured slave count as of this cycle.</summary>
    public required EtherCatMasterState MasterState { get; init; }

    /// <summary>Null when this cycle's frame counter read (IG 0x0C) did not answer.</summary>
    public required FrameStatistics? FrameStatistics { get; init; }

    /// <summary>
    /// Which decorating reads did not answer in the cycle that produced this snapshot.
    /// <see cref="EtherCatReads.None"/> for a cycle in which everything answered.
    /// </summary>
    public required EtherCatReads IncompleteReads { get; init; }

    /// <summary>One entry per configured slave, in the order the master reported them.</summary>
    public required IReadOnlyList<EtherCatSlaveSnapshot> Slaves { get; init; }

    /// <summary>
    /// One entry per sync unit. Always empty today — see
    /// <see cref="IEtherCatClient.GetSyncUnitsAsync"/>.
    /// </summary>
    public required IReadOnlyList<SyncUnitInfo> SyncUnits { get; init; }
}

/// <summary>
/// One slave as of one poll cycle.
///
/// <para>
/// The flat fields come from the configured-slave list — a gating read, so a snapshot only exists
/// when they were answered. The three nullable sub-records come from decorating reads, and null
/// means THAT READ DID NOT ANSWER, never that the slave reported nothing.
/// <see cref="Scanned"/>'s null has two possible meanings — see its own doc for what actually tells
/// them apart. <see cref="EtherCatSnapshot.IncompleteReads"/> only says whether a read of that kind
/// failed SOMEWHERE this cycle, for SOME slave — not for which one, and not that it explains any
/// particular slave's own null.
/// </para>
/// </summary>
public sealed class EtherCatSlaveSnapshot
{
    /// <summary>See <see cref="EtherCatSlaveInfo.PhysicalAddress"/>.</summary>
    public required ushort PhysicalAddress { get; init; }

    /// <summary>See <see cref="EtherCatSlaveInfo.AutoIncrementAddress"/>.</summary>
    public required ushort AutoIncrementAddress { get; init; }

    /// <summary>See <see cref="EtherCatSlaveInfo.Name"/>.</summary>
    public required string Name { get; init; }

    /// <summary>See <see cref="EtherCatSlaveInfo.Type"/>.</summary>
    public required string Type { get; init; }

    /// <summary>See <see cref="EtherCatSlaveInfo.CurrentState"/>.</summary>
    public required string CurrentState { get; init; }

    /// <summary>See <see cref="EtherCatSlaveInfo.RequestedState"/>.</summary>
    public required string RequestedState { get; init; }

    /// <summary>See <see cref="EtherCatSlaveInfo.IsPresent"/>.</summary>
    public required bool IsPresent { get; init; }

    /// <summary>See <see cref="EtherCatSlaveInfo.HasError"/>.</summary>
    public required bool HasError { get; init; }

    /// <summary>See <see cref="EtherCatSlaveInfo.IsDisabled"/>.</summary>
    public required bool IsDisabled { get; init; }

    /// <summary>
    /// Configured identity, init-error state and port list. Null when this cycle's detail read
    /// (IG 0x11/0x09/0x12) did not answer.
    /// </summary>
    public required EtherCatSlaveDetail? Detail { get; init; }

    /// <summary>Null when this cycle's CRC counter read (IG 0x12) did not answer.</summary>
    public required SlaveErrorCounters? ErrorCounters { get; init; }

    /// <summary>
    /// This slave's entry in the bus-wide scanned identity list. Null means either that the scan
    /// did not answer at all or that this configured slave is not physically on the bus — opposite
    /// facts that <see cref="EtherCatReads.ScannedIdentities"/> does NOT reliably tell apart: that
    /// flag is set by ANY slave's scanned-identity failure this cycle, including one that is not
    /// this slave, so it can be set while this slave is genuinely absent and entirely unrelated to
    /// it. A cycle where the flag is CLEAR is the reliable case — it guarantees every null
    /// <see cref="Scanned"/> in that snapshot is genuine absence, because nothing in the
    /// scanned-identity domain failed at all.
    ///
    /// <para>
    /// A third state lives INSIDE a non-null <see cref="EtherCatScannedSlave"/>:
    /// its <see cref="EtherCatScannedSlave.VendorId"/> (and the rest of
    /// its identity fields) are null when the scan as a whole answered and did report this slave's
    /// address, but that slave's own per-slave identity read did not. <see cref="Scanned"/> itself
    /// being null, versus non-null with null identity fields, is what separates "this slave was not
    /// in the scan at all" from "this slave was scanned but its identity did not answer" —
    /// <see cref="EtherCatReads.ScannedIdentities"/> is set for both cases (and for an unrelated
    /// slave's failure besides), so it cannot make that distinction for one slave on its own.
    /// </para>
    /// </summary>
    public required EtherCatScannedSlave? Scanned { get; init; }
}
