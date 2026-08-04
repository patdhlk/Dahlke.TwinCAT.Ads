namespace Dahlke.EtherCAT.Diagnostics;

/// <summary>
/// The reads that decorate an EtherCAT snapshot without feeding change detection, named so a
/// snapshot can say which of them did not answer in the cycle that produced it.
///
/// <para>
/// This is distinct from <c>diagnosticsDegraded</c>, and the distinction is the point.
/// <c>diagnosticsDegraded</c> means adsify is BLIND: a read the whole snapshot depends on failed,
/// no reading was taken, the last known-good one is being served and no events are emitted. A flag
/// here means adsify read the master successfully but not completely — the snapshot is fresh, its
/// events are real, and the fields fed by the named read are absent rather than filled in.
/// </para>
/// <para>
/// <b>Load-bearing, but only at the cycle level — not a per-slave confirmation.</b> A flag here
/// makes an otherwise-unexplained null explainable: "a read of this kind did not fully answer
/// SOMEWHERE this cycle." It does not confirm that a failed read is what produced the null on any
/// ONE slave you happen to be looking at. <see cref="ScannedIdentities"/> is where this bites
/// hardest: it is set when the bus-wide scanned-identity list failed to answer at all, OR when at
/// least one slave's own identity within an otherwise-successful list did not — so a slave that is
/// genuinely absent from the bus can show this flag set in the very same cycle purely because some
/// OTHER slave's identity read failed, with no relation to the absent one at all. What actually
/// happened to any ONE slave is what that slave's own field says — see
/// <see cref="EtherCatSlaveSnapshot.Scanned"/> — not this flag.
/// </para>
/// </summary>
[Flags]
public enum EtherCatReads
{
    /// <summary>Every decorating read answered this cycle — nothing here explains an absent field.</summary>
    None = 0,

    /// <summary>The master's frame counter block (IG 0x0C).</summary>
    FrameStatistics = 1 << 0,

    /// <summary>
    /// The bus-wide scanned identity list (IG 0x06/0x07) failed to answer at all, or at least one
    /// slave's own scanned identity (IG 0x11) within an otherwise-successful list did not.
    /// </summary>
    ScannedIdentities = 1 << 1,

    /// <summary>At least one slave's detail read (IG 0x11/0x09/0x12) did not answer.</summary>
    SlaveDetail = 1 << 2,

    /// <summary>At least one slave's CRC counter read (IG 0x12) did not answer.</summary>
    SlaveErrorCounters = 1 << 3,
}
