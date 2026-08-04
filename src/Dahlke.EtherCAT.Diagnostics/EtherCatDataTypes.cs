namespace Dahlke.EtherCAT.Diagnostics;

/// <summary>
/// Master info from port 300 (AMSPORT_R0_IO) enumeration, IG 0x5000.
/// </summary>
public sealed class EtherCatMasterInfo
{
    /// <summary>
    /// Ordinal assigned by discovery order (0, 1, 2…), not a value read from the master — the
    /// synthetic "assumed" master used when nothing answers is always 0.
    /// </summary>
    public required int DeviceId { get; init; }

    /// <summary>Display name, e.g. <c>"EtherCAT Master 0"</c> or the synthetic assumed-master label.</summary>
    public required string Name { get; init; }

    /// <summary>The master's own AMS NetId (port 0xFFFF) — distinct from the PLC's own NetId.</summary>
    public required string AmsNetId { get; init; }
}

/// <summary>A master's current EtherCAT state and configured slave count, as of one read.</summary>
public sealed class EtherCatMasterState
{
    /// <summary>
    /// The master's current state: <c>"Init"</c>, <c>"PreOp"</c>, <c>"Bootstrap"</c>,
    /// <c>"SafeOp"</c> or <c>"Op"</c> per ETG.1000, <c>"Unknown"</c> for a raw state nibble of 0,
    /// or <c>"Unknown(0xNN)"</c> for any other undefined nibble.
    /// </summary>
    public required string CurrentState { get; init; }

    /// <summary>The state the master has been asked to transition to.</summary>
    public required string RequestedState { get; init; }

    /// <summary>Number of slaves the master reports as configured (IG 0x06).</summary>
    public required int SlaveCount { get; init; }
}

/// <summary>
/// Frame counter statistics from IG 0x0C on port 0xFFFF.
/// Field names match TwinCAT System Manager terminology:
///   Cyclic = cyclic EtherCAT frames
///   Queued = queued (acyclic/mailbox) frames
/// </summary>
public sealed class FrameStatistics
{
    /// <summary>Cyclic (real-time process data) frames the master has sent.</summary>
    public long CyclicSendFrames { get; init; }

    /// <summary>Queued (acyclic/mailbox) frames the master has sent.</summary>
    public long QueuedSendFrames { get; init; }

    /// <summary>Cyclic frames lost.</summary>
    public long CyclicLostFrames { get; init; }

    /// <summary>Queued (acyclic/mailbox) frames lost.</summary>
    public long QueuedLostFrames { get; init; }

    /// <summary>
    /// Cyclic frames per second, or null when it could not be derived — the rate is a delta
    /// against the previous cycle's counters, so it is unavailable on the first cycle and for one
    /// cycle after a dropped counter read.
    /// </summary>
    public double? CyclicFramesPerSecond { get; init; }

    /// <summary>Queued frames per second, or null. See <see cref="CyclicFramesPerSecond"/>.</summary>
    public double? QueuedFramesPerSecond { get; init; }

    /// <summary>
    /// Always 0. IG 0x0C carries five counters and none of them is a cyclic Tx/Rx error count, and
    /// no other index group supplies one — this is a fixed constant, not a reading of zero errors.
    /// </summary>
    public long CyclicTxRxErrors { get; init; }

    /// <summary>Always 0, for the same reason as <see cref="CyclicTxRxErrors"/>.</summary>
    public long QueuedTxRxErrors { get; init; }
}

/// <summary>
/// One slave in the master's configured-slave list (IG 0x06/0x07/0x09) — the gating read a
/// snapshot cannot exist without. See <see cref="IEtherCatClient.GetConfiguredSlavesAsync"/>.
/// </summary>
public sealed class EtherCatSlaveInfo
{
    /// <summary>The slave's fixed address on the bus (IG 0x07).</summary>
    public required ushort PhysicalAddress { get; init; }

    /// <summary>
    /// Zero-based position of this slave within the configured-slave list, assigned by enumeration
    /// order — not a value separately read from the master.
    /// </summary>
    public required ushort AutoIncrementAddress { get; init; }

    /// <summary>
    /// The slave's decoded device name (e.g. <c>"EL1008"</c>), or <c>"Slave {address}"</c> when
    /// its identity could not be decoded.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The slave's decoded device type, or <c>"Unknown"</c> when the identity read failed or the
    /// vendor/product pair is not recognised.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// The slave's current state: <c>"Init"</c>, <c>"PreOp"</c>, <c>"Bootstrap"</c>,
    /// <c>"SafeOp"</c> or <c>"Op"</c> per ETG.1000, <c>"Unknown"</c> for a raw state nibble of 0,
    /// or <c>"Unknown(0xNN)"</c> for any other undefined nibble.
    /// </summary>
    public required string CurrentState { get; init; }

    /// <summary>The state this slave has been asked to transition to.</summary>
    public required string RequestedState { get; init; }

    /// <summary>
    /// Whether the slave is currently on the bus — true when either its device state or link
    /// state byte is non-zero.
    /// </summary>
    public required bool IsPresent { get; init; }

    /// <summary>Whether the slave's device state reports the error flag (bit 4 of IG 0x09).</summary>
    public required bool HasError { get; init; }

    /// <summary>
    /// True when both the device state and link state bytes read as zero — configured but not
    /// linked to the bus.
    /// </summary>
    public required bool IsDisabled { get; init; }
}

/// <summary>
/// One slave's entry in the bus-wide scan (<see cref="EtherCatClient.GetScannedSlavesAsync"/>).
///
/// <see cref="PhysicalAddress"/> is always known — it comes from the master's address list
/// (IG 0x07), a read this type's whole containing list already answers null for on failure. The
/// four identity fields are a SEPARATE per-slave read (IG 0x11) and are null together, never
/// individually, when that read for THIS slave's address did not answer: a dropped identity read
/// must not blank the address that read didn't touch, and must not synthesise a fabricated
/// all-zero device either. See <see cref="EtherCatClient.GetScannedSlavesAsync"/> for why the
/// entry stays in the list either way, rather than being omitted — an omitted entry already means
/// something else (this address is not on the bus at all).
/// </summary>
public sealed class EtherCatScannedSlave
{
    /// <summary>
    /// The slave's fixed address (IG 0x07). Always known whenever this entry exists — see the
    /// class doc for why an entry with a failed identity read still carries one.
    /// </summary>
    public required ushort PhysicalAddress { get; init; }

    /// <summary>Null when this slave's identity read (IG 0x11) did not answer. See the class doc.</summary>
    public required uint? VendorId { get; init; }

    /// <summary>Null together with <see cref="VendorId"/>. See the class doc.</summary>
    public required uint? ProductCode { get; init; }

    /// <summary>Null together with <see cref="VendorId"/>. See the class doc.</summary>
    public required uint? RevisionNumber { get; init; }

    /// <summary>Null together with <see cref="VendorId"/>. See the class doc.</summary>
    public required uint? SerialNumber { get; init; }
}

/// <summary>
/// Configured identity, scanned identity, init-error state and port list for one slave
/// (IG 0x11/0x09/0x12).
///
/// <para>
/// <b>Known limitation:</b> the scanned identity fields (<see cref="ScannedVendorId"/> and the
/// rest) are populated from the same read as the configured ones, so they are always equal to
/// them, and <see cref="IdentityMatch"/> is therefore always <see langword="true"/> — this ADS
/// interface has no read that tells what is actually wired on the bus apart from what TwinCAT is
/// configured to expect. A genuine identity mismatch cannot be detected through this type today.
/// </para>
/// </summary>
public sealed class EtherCatSlaveDetail
{
    /// <summary>Vendor id from this slave's configured identity object (IG 0x11).</summary>
    public required uint ConfiguredVendorId { get; init; }

    /// <summary>Product code from this slave's configured identity object (IG 0x11).</summary>
    public required uint ConfiguredProductCode { get; init; }

    /// <summary>Revision number from this slave's configured identity object (IG 0x11).</summary>
    public required uint ConfiguredRevisionNumber { get; init; }

    /// <summary>Serial number from this slave's configured identity object (IG 0x11).</summary>
    public required uint ConfiguredSerialNumber { get; init; }

    /// <summary>
    /// Vendor id as scanned off the bus. See the class remarks — currently always equal to
    /// <see cref="ConfiguredVendorId"/>.
    /// </summary>
    public required uint ScannedVendorId { get; init; }

    /// <summary>
    /// Product code as scanned off the bus. See the class remarks — currently always equal to
    /// <see cref="ConfiguredProductCode"/>.
    /// </summary>
    public required uint ScannedProductCode { get; init; }

    /// <summary>
    /// Revision number as scanned off the bus. See the class remarks — currently always equal to
    /// <see cref="ConfiguredRevisionNumber"/>.
    /// </summary>
    public required uint ScannedRevisionNumber { get; init; }

    /// <summary>
    /// Serial number as scanned off the bus. See the class remarks — currently always equal to
    /// <see cref="ConfiguredSerialNumber"/>.
    /// </summary>
    public required uint ScannedSerialNumber { get; init; }

    /// <summary>
    /// Whether the scanned identity matches the configured one. See the class remarks — currently
    /// always <see langword="true"/>, since both identities are read from the same source.
    /// </summary>
    public required bool IdentityMatch { get; init; }

    /// <summary>
    /// Whether this slave's device state reports the error flag (bit 4 of IG 0x09) — the same bit
    /// <see cref="EtherCatSlaveInfo.HasError"/> reports for the configured-slave list.
    /// </summary>
    public required bool InitError { get; init; }

    /// <summary>This slave's per-port link and configuration state (IG 0x12).</summary>
    public required IReadOnlyList<SlavePortInfo> Ports { get; init; }
}

/// <summary>
/// One ESC port's link and configuration state, from the per-slave CRC counter block (IG 0x12) —
/// a port the master reported a counter for is linked, and the rest report the unconfigured
/// default.
/// </summary>
public sealed class SlavePortInfo
{
    /// <summary>Port label — <c>"A"</c>, <c>"B"</c>, <c>"C"</c> or <c>"D"</c>.</summary>
    public required string Port { get; init; }

    /// <summary>
    /// Port medium. Always <c>"EBus"</c> when linked and <c>"none"</c> when not: the ADS interface
    /// this reads from does not expose the actual physical medium, so a cable-connected branch
    /// (e.g. an EK1122 junction) reads identically to a continuous EBus trace.
    /// </summary>
    public required string Physic { get; init; }

    /// <summary>Whether the master reported a CRC counter for this port.</summary>
    public required bool Configured { get; init; }

    /// <summary>
    /// Whether this port currently has a link. Mirrors <see cref="Configured"/> exactly — the ADS
    /// interface reports one flag, not the two independently, so a port that is configured but
    /// not linked cannot be told apart from one that is not configured at all.
    /// </summary>
    public required bool LinkState { get; init; }
}

/// <summary>Per-slave CRC error counters for one poll cycle (IG 0x12).</summary>
public sealed class SlaveErrorCounters
{
    /// <summary>The slave's fixed address these counters belong to.</summary>
    public required ushort PhysicalAddress { get; init; }

    /// <summary>Per-port CRC counters, one entry for each port the master reported a counter for.</summary>
    public required IReadOnlyList<PortErrorCounters> Ports { get; init; }

    /// <summary>
    /// Always 0. IG 0x12 carries no abnormal-state-change count and no other index group supplies
    /// one — this is a fixed constant, not a reading of zero state changes.
    /// </summary>
    public required int AbnormalStateChanges { get; init; }
}

/// <summary>One port's CRC error counters, from the per-slave counter block (IG 0x12).</summary>
public sealed class PortErrorCounters
{
    /// <summary>Port label — <c>"A"</c>, <c>"B"</c>, <c>"C"</c> or <c>"D"</c>.</summary>
    public required string Port { get; init; }

    /// <summary>
    /// CRC error count the master reports for this port — the one genuine reading in this type.
    /// See <see cref="ForwardedCrcErrors"/> and <see cref="LostLinkCount"/> for the two that
    /// are not.
    /// </summary>
    public required int CrcErrors { get; init; }

    /// <summary>
    /// Always 0. IG 0x12 carries one CRC counter per port and nothing else — no forwarded-CRC
    /// count is ever read, so this is a fixed constant rather than a reading of zero.
    /// </summary>
    public required int ForwardedCrcErrors { get; init; }

    /// <summary>Always 0, for the same reason as <see cref="ForwardedCrcErrors"/>.</summary>
    public required int LostLinkCount { get; init; }
}

/// <summary>
/// One EtherCAT sync unit's fault state. Never populated with real data today —
/// <see cref="IEtherCatClient.GetSyncUnitsAsync"/> is unimplemented and always returns an empty
/// list; see its own doc for why.
/// </summary>
public sealed class SyncUnitInfo
{
    /// <summary>The sync unit's identifier.</summary>
    public required int Id { get; init; }

    /// <summary>Whether this sync unit currently reports a fault.</summary>
    public required bool HasError { get; init; }

    /// <summary>Cumulative fault count for this sync unit.</summary>
    public required long FaultCounter { get; init; }

    /// <summary>Frames missed by this sync unit.</summary>
    public required long FramesMissed { get; init; }

    /// <summary>Fixed addresses of the slaves that belong to this sync unit.</summary>
    public required IReadOnlyList<ushort> Slaves { get; init; }
}

/// <summary>
/// Result of a CoE (CANopen over EtherCAT) SDO upload.
/// </summary>
public sealed class CoeReadResult
{
    /// <summary>
    /// Whether the read completed with data. False for a mailbox absence, an unrecognised object,
    /// or any other ADS failure — see <see cref="Reason"/>.
    /// </summary>
    public required bool Succeeded { get; init; }

    /// <summary>The object's raw bytes. Empty when <see cref="Succeeded"/> is false.</summary>
    public required byte[] Data { get; init; }

    /// <summary>Why the read failed, or <see cref="CoeFailureReason.None"/> when it succeeded.</summary>
    public CoeFailureReason Reason { get; init; } = CoeFailureReason.None;

    /// <summary>Underlying ADS error, kept for diagnostics even when <see cref="Reason"/> is set.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Why a CoE read failed, classified so the API can answer stably rather than leaking whichever
/// ADS error the router happened to produce.
/// </summary>
public enum CoeFailureReason
{
    /// <summary>The read succeeded; there is no failure to classify.</summary>
    None = 0,

    /// <summary>
    /// The slave cannot serve CoE at all — couplers and plain I/O terminals have no mailbox.
    ///
    /// The same physical condition surfaces as two different ADS errors depending on whether the
    /// AMS router has already learned the port is absent: PortNotConnected once it has, and a
    /// plain timeout the first time. Both collapse to this reason so a caller does not see the
    /// status flip between requests for an unchanging property of the hardware.
    /// </summary>
    NoMailbox,

    /// <summary>The slave has a mailbox but the object or subindex is not in its dictionary.</summary>
    ObjectNotFound,

    /// <summary>Anything else — a genuine transport or device fault.</summary>
    AdsError,
}
