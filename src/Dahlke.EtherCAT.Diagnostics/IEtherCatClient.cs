namespace Dahlke.EtherCAT.Diagnostics;

/// <summary>
/// ADS client for EtherCAT diagnostics. Talks to ADS port 300 (AMSPORT_R0_IO)
/// for master enumeration and to each master's own AMS NetId on port 0xFFFF
/// (AMSPORT_R0_MASTER) for state/slave/sync-unit reads.
/// Separate from IAdsConnection which targets the PLC runtime on port 851.
///
/// <para>
/// <b>Failed reads are reported as <see langword="null"/>, never as a value.</b> Six methods
/// here answer <see langword="null"/> when a read their result depends on did not come back, and a
/// value only when the master actually answered: <see cref="GetMasterStateAsync"/>,
/// <see cref="GetConfiguredSlavesAsync"/>, <see cref="GetScannedSlavesAsync"/>,
/// <see cref="GetFrameStatisticsAsync"/>, <see cref="GetSlaveDetailAsync"/> and
/// <see cref="GetSlaveErrorCountersAsync"/>. That keeps
/// "the master says there are no slaves" (<c>[]</c>) distinct from "the slave-count read failed"
/// (<see langword="null"/>), and "the master reports a state code outside ETG.1000's set"
/// (<c>CurrentState == "Unknown"</c> for a raw state nibble of 0, or <c>"Unknown(0xNN)"</c> for
/// any other undefined nibble) distinct from "the master-state read failed"
/// (<see langword="null"/>). A caller that collapses those cannot tell a bus outage from a healthy
/// bus.
/// </para>
/// <para>
/// Which failures matter is the caller's policy, not this interface's:
/// <c>EtherCatMonitor</c> treats a null from the first two as the master's diagnostics being
/// unavailable for that cycle, and a null from <see cref="GetScannedSlavesAsync"/> as identity
/// fields being absent — scanned identity feeds no change detection.
/// </para>
/// </summary>
public interface IEtherCatClient
{
    /// <summary>
    /// Enumerates the EtherCAT masters reachable from this PLC. Never empty — a PLC with no
    /// master answering any candidate Net ID still returns one synthetic entry
    /// (<see cref="EtherCatMasterInfo.DeviceId"/> 0) so a total bus outage is reported rather than
    /// silently polling nothing.
    /// </summary>
    Task<IReadOnlyList<EtherCatMasterInfo>> GetMastersAsync(string amsNetId, CancellationToken ct);

    /// <summary>
    /// Reads the master's own EtherCAT state and configured slave count.
    /// Returns <see langword="null"/> when either read fails or answers short — the master's state
    /// is then unknown to adsify, which is not the same as the master reporting an unknown state.
    /// A master that answers with a state code outside ETG.1000's set is reported as a value:
    /// <see cref="EtherCatMasterState.CurrentState"/> is <c>"Unknown"</c> when the raw state nibble
    /// is 0, and <c>"Unknown(0xNN)"</c> carrying the raw byte for any other undefined nibble.
    /// </summary>
    Task<EtherCatMasterState?> GetMasterStateAsync(string masterAmsNetId, CancellationToken ct);

    /// <summary>
    /// Reads the master's frame counter block (IG 0x0C). Returns <see langword="null"/> when that
    /// read fails or answers short — zeroed counters would be indistinguishable from a master that
    /// has genuinely sent no frames, and they also poison the NEXT cycle's per-second rates, which
    /// are a delta against them.
    /// </summary>
    Task<FrameStatistics?> GetFrameStatisticsAsync(string masterAmsNetId, CancellationToken ct);

    /// <summary>
    /// Reads the configured slave list with each slave's fixed address, state and link state.
    /// Returns <see langword="null"/> when the slave-count, address or state read fails or answers
    /// short, and an empty list when the master reports zero configured slaves.
    /// Addresses are never synthesised: a slave appears here only with the address the master gave.
    /// </summary>
    Task<IReadOnlyList<EtherCatSlaveInfo>?> GetConfiguredSlavesAsync(string masterAmsNetId, CancellationToken ct);

    /// <summary>
    /// Reads each slave's identity object. Returns <see langword="null"/> when the slave-count or
    /// address read fails or answers short, and an empty list when the master reports zero
    /// configured slaves. A slave whose identity read fails is still listed, at the address the
    /// master reported, with its identity fields (<see cref="EtherCatScannedSlave.VendorId"/> and
    /// the rest) absent rather than zeroed.
    /// </summary>
    Task<IReadOnlyList<EtherCatScannedSlave>?> GetScannedSlavesAsync(string masterAmsNetId, CancellationToken ct);

    /// <summary>
    /// Reads one slave's identity (IG 0x11), init-error state (IG 0x09) and port block (IG 0x12).
    /// Returns <see langword="null"/> when ANY of those three fails, discarding the ones that
    /// answered — the same whole-call rule <see cref="GetConfiguredSlavesAsync"/> follows.
    /// It reports less than adsify knows rather than more: the alternative is zero-filled identity
    /// fields and an empty port list, which a caller reads as a real bus fault.
    /// </summary>
    Task<EtherCatSlaveDetail?> GetSlaveDetailAsync(string masterAmsNetId, ushort physicalAddress, CancellationToken ct);

    /// <summary>
    /// Reads one slave's CRC counter block (IG 0x12). Returns <see langword="null"/> when that read
    /// fails or answers shorter than one counter. Zeroed counters read as a fault-free port, which
    /// is the exact reading an operator would act on.
    /// </summary>
    Task<SlaveErrorCounters?> GetSlaveErrorCountersAsync(string masterAmsNetId, ushort physicalAddress, CancellationToken ct);
    /// <summary>
    /// Clears the error counters of a single slave. Returns <see langword="false"/> if the
    /// master rejected the write — callers must not report success in that case.
    /// </summary>
    Task<bool> ResetSlaveErrorCountersAsync(string masterAmsNetId, ushort physicalAddress, CancellationToken ct);

    /// <summary>
    /// Reads sync unit fault state. Always returns an empty list today — no ADS index group for
    /// sync units has been identified on this master; see the implementation's own remarks for
    /// the candidates that were ruled out.
    /// </summary>
    Task<IReadOnlyList<SyncUnitInfo>> GetSyncUnitsAsync(string masterAmsNetId, CancellationToken ct);

    /// <summary>
    /// Reads a single CoE object (SDO upload) from a slave's object dictionary.
    ///
    /// Unlike every other method here this addresses the slave by <b>ADS port</b> rather than by
    /// IO offset — see <see cref="EtherCatClient"/> for why. Slaves without a mailbox never
    /// answer, so this is strictly on-demand and must not be called from the polling loop.
    /// </summary>
    Task<CoeReadResult> ReadCoeObjectAsync(
        string masterAmsNetId,
        ushort physicalAddress,
        ushort index,
        byte subIndex,
        int timeoutMs,
        int maxBytes,
        CancellationToken ct);

    /// <summary>
    /// Writes a single CoE object (SDO download) into a slave's object dictionary.
    ///
    /// Addressed exactly like <see cref="ReadCoeObjectAsync"/> — by <b>ADS port</b>, on-demand
    /// only, never from the polling loop.
    ///
    /// <para>
    /// <b>A refusal is a result, not an exception.</b> The whole point of the return value is the
    /// slave's own reason: <see cref="CoeWriteResult.Reason"/> distinguishes a slave with no mailbox
    /// from an object that is not in its dictionary from an <see cref="CoeFailureReason.SdoAbort"/>,
    /// and an abort carries the device's verbatim code in
    /// <see cref="CoeWriteResult.AbortCode"/> — read-only object, value out of range, local control.
    /// During commissioning that code is the difference between "fix the value" and "put the drive
    /// back in remote".
    /// </para>
    /// <para>
    /// <b>This library does not read the value back and does not verify it.</b> A successful result
    /// means the slave accepted the download, which is not the same as the parameter now holding
    /// what you sent: a device may clamp, round or store to a shadow copy pending a save command
    /// (0x1010). Call <see cref="ReadCoeObjectAsync"/> if you need to know what it kept.
    /// </para>
    /// <para>
    /// <b>A write that gets no answer is retried, so it can reach the slave twice.</b> The raw
    /// channel's <c>RetryCount</c> (1 by default) applies here as it does to every other call, which
    /// is right for a parameter store — writing the same value twice leaves the same value — and
    /// wrong for an object whose write is a COMMAND: a save-to-EEPROM trigger such as 0x1010:01, a
    /// counter, a queue push. A slave that received the first write and only lost the answer cannot
    /// distinguish the retry from a fresh request. Configure <c>RawChannels:RetryCount</c> to 0 in a
    /// host that writes such objects.
    /// </para>
    /// <para>
    /// <b>Nothing here understands CoE data types.</b> <paramref name="data"/> goes on the wire as
    /// given, so its length and byte order must match the object's declared type — little-endian,
    /// and exactly as wide as the slave expects. A drive answers a mismatch with abort 0x06070010
    /// rather than guessing, and this method reports that abort rather than silently padding.
    /// </para>
    /// </summary>
    /// <param name="masterAmsNetId">The EtherCAT master's own AMS Net ID.</param>
    /// <param name="physicalAddress">The slave's fixed address, used as the ADS port.</param>
    /// <param name="index">CoE object index, e.g. 0x8010.</param>
    /// <param name="subIndex">CoE subindex, 0 for a single-value object.</param>
    /// <param name="data">The object's new value, already encoded for its declared type.</param>
    /// <param name="timeoutMs">Bound for EACH attempt — see <c>EtherCatClient</c> on retry.</param>
    /// <param name="ct">Cancels the operation, and is the one input that surfaces as an exception.</param>
    Task<CoeWriteResult> WriteCoeObjectAsync(
        string masterAmsNetId,
        ushort physicalAddress,
        ushort index,
        byte subIndex,
        ReadOnlyMemory<byte> data,
        int timeoutMs,
        CancellationToken ct);
}
