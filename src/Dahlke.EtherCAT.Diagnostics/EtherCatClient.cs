using Dahlke.TwinCAT.Ads;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using TwinCAT.Ads;

namespace Dahlke.EtherCAT.Diagnostics;

/// <summary>
/// TwinCAT ADS implementation of <see cref="IEtherCatClient"/>.
///
/// Connection model (validated against real CX5140 hardware + Beckhoff InfoSys docs):
///   - The EtherCAT master has its own AMS NetId, derived from the PLC's NetId
///     by changing byte 5 (e.g., PLC=192.168.1.136.1.1 → master=192.168.1.136.3.1).
///   - The TCP route goes through the PLC's IP — no separate AMS route needed.
///   - The PLC's AMS router internally forwards requests to the EC master device.
///
/// Two ADS access patterns (per Beckhoff InfoSys "ADS Interface" documentation):
///
///   1. Master-level reads: AmsNetId=masterNetId, Port=0xFFFF
///      IG 0x06 IO 0x00 → uint16: slave count
///      IG 0x07 IO 0x00 → uint16[]: slave address list (fixed addresses)
///      IG 0x09 IO 0x00 → byte[]: all slave states (2 bytes per slave: state + link)
///      IG 0x0C IO 0x00 → 5×uint32: frame counter statistics
///      IG 0x12 IO 0x00 → uint32[]: CRC error count per slave
///
///   2. Per-slave reads: AmsNetId=masterNetId, Port=slaveFixedAddress
///      IG 0x09 IO 0x00 → 2 bytes: {deviceState, linkState} for this slave
///      IG 0x11 IO 0x00 → 4×uint32: identity (vendor, product, revision, serial)
///      IG 0x12 IO 0x00 → one uint32 CRC counter per *linked* port — 4 bytes per linked
///                        port, NOT a fixed 16. The response length is the port count.
///
/// EtherCAT state encoding: 1=Init, 2=PreOp, 3=Bootstrap, 4=SafeOp, 8=Op
///
/// Reliability: reads on port 0xFFFF can be intermittent under real-time load. Every read and
/// write here goes through <see cref="IAdsRawChannel"/>, obtained per call from
/// <see cref="IAdsRawChannelFactory.Get"/> and never disposed or cached in a field — the channel
/// owns connection lifetime, retry and the per-attempt timeout, which this class no longer
/// manages itself. The per-attempt bound differs by call, though: the raw read seam
/// (<c>ReadRawAsync</c>) and the counter-reset write pass <see cref="DefaultTimeout"/> (10 s)
/// explicitly, so <c>RawChannels:TimeoutMs</c> is inert for those. <c>GetMastersAsync</c>'s master
/// probe cannot — <see cref="IAdsRawChannel.ReadStateAsync"/> has no <see cref="TimeSpan"/>
/// overload — so it is bounded by <c>ProbeTimeoutMs</c> (2 s) via a linked
/// <see cref="CancellationTokenSource"/> AND by the library's configured
/// <c>RawChannels:TimeoutMs</c>, whichever is shorter. <c>ReadCoeObjectAsync</c> passes the
/// caller's own <c>timeoutMs</c> instead of either constant.
///
/// <c>GetMastersAsync</c> caches which candidate Net ID(s) the last successful discovery found,
/// per PLC, for up to <see cref="FullSweepInterval"/> — see <see cref="_knownMasters"/>. Steady
/// state (the common case for <c>EtherCatMonitor</c>'s once-per-cycle call, within that
/// window) therefore re-probes only the cached master(s), not every candidate
/// <see cref="DeriveMasterCandidates"/> could name. A cached master that stops answering, or a
/// cache older than <see cref="FullSweepInterval"/>, forces a full re-sweep of every candidate in
/// that same call — the age-based trigger is what catches a SECOND master joining an
/// already-cached bus, which failure-driven invalidation alone would not, since steady-state
/// verification only ever re-probes Net IDs already in the cache. Measured in a prior
/// hardware-verification session against a real CX/EK1100 rack (see
/// <c>docs/superpowers/plans/2026-07-30-hardware-verification-results.md</c>, now addressed on
/// this branch): that rig's single-master bus, polling once a second, logged roughly 283
/// <see cref="AdsErrorException"/>s over 65 cycles from failed candidate probes. That rig's PLC
/// Net ID happens to make <see cref="DeriveMasterCandidates"/> derive exactly 5 candidates — it
/// derives UP TO 5 in general, fewer when the PLC's own byte 5 already falls in 2..5, see its own
/// doc — of which 1 answered, predicting 4 × 65 = 260 failed probes; the remaining 23 are
/// unexplained in that session's record, and this change does not explain them either. This
/// change does not alter <see cref="DeriveMasterCandidates"/> or the per-candidate probe itself,
/// so the steady-state reduction follows directly from THAT RIG's candidate count — 5
/// probes/cycle before this change, 1 probe/cycle in steady state after, a figure specific to
/// that rig's topology, not a general one — rather than a fresh hardware measurement taken for
/// this change.
///
/// Retry, per call site — <c>RawChannels:RetryCount</c> (1, i.e. two attempts) applies to some of
/// these and not others, and the "not others" is a REGRESSION, not an oversight: those sites
/// bypassed adsify's own hand-rolled retry helper before this class moved onto
/// <see cref="IAdsRawChannel"/>, so they went from zero retry to one, not from one retry to zero.
/// <list type="table">
///   <item><term><c>ReadRawAsync</c> (the read seam every master/slave diagnostic goes
///     through)</term><description>2×10 s before and after — unchanged, this site already shared
///     the old retry helper.</description></item>
///   <item><term><c>GetMastersAsync</c>'s master probe</term><description>1×2 s before and after —
///     unchanged in practice: adsify's own 2 s linked token is shorter than the library's
///     configured <c>TimeoutMs</c> (10 s default), so it is the CALLER's token from
///     <c>AdsRawChannel.RunAttemptsAsync</c>'s point of view and is never retried, exactly as the
///     old single-attempt probe never was.</description></item>
///   <item><term><see cref="ResetSlaveErrorCountersAsync"/>'s write</term><description>1×10 s
///     before, <b>2×10 s after</b> — this call bypassed the old retry helper entirely (a single
///     attempt, no loop), so the library's <c>RetryCount: 1</c> is new latency on a genuine
///     failure, not saved latency.</description></item>
///   <item><term><see cref="ReadCoeObjectAsync"/></term><description>1×<c>CoeTimeoutMs</c> before,
///     <b>2×<c>CoeTimeoutMs</c> after</b> — same story as the counter-reset write: this call also
///     bypassed the old retry helper. A slave that answers with an ADS error code (mailbox present
///     but rejects the object, or the router already knows the port has no mailbox) is unaffected
///     — never retried, either version. A slave whose mailbox absence surfaces as a bare timeout
///     now pays that timeout twice before giving up.</description></item>
/// </list>
/// See <c>DEVELOPMENT.md</c>'s "<c>RawChannels</c>" section for the same table aimed at an
/// operator tuning <c>appsettings.json</c>.
/// </summary>
internal sealed class EtherCatClient : IEtherCatClient
{
    private readonly ILogger<EtherCatClient> _logger;
    private readonly IAdsRawChannelFactory _channels;

    // -- Master-discovery cache ----------------------------------------------------
    //
    // Keyed by the PLC's OWN AmsNetId (the argument GetMastersAsync receives), never
    // globally — a host can serve several PLC targets, each with its own bus. The value is the
    // set of candidate Net IDs that answered the LAST successful discovery for that PLC, in
    // deviceId order, plus when that discovery ran. GetMastersAsync re-verifies every cached Net
    // ID on every call made within FullSweepInterval of that timestamp: as long as all of them
    // still answer, that verification IS the whole call (one round trip per master instead of
    // DeriveMasterCandidates' up-to-five) — see GetMastersAsync for the invalidate-and-resweep
    // path taken when one does not, and for the periodic resweep once FullSweepInterval elapses.
    //
    // The synthetic "assumed master 0" fallback (emitted when NOTHING answers) is deliberately
    // never written here — see GetMastersAsync's fallback branch for why.
    //
    // ConcurrentDictionary, not a plain Dictionary with a lock: this class is registered as a
    // singleton (see Program.cs) and the polling loop can call GetMastersAsync concurrently for
    // the same or different PLCs. Every access below is a single ConcurrentDictionary operation
    // (TryGetValue / indexer-set / TryRemove) with no lock held across an await.
    private readonly ConcurrentDictionary<string, CachedMasters> _knownMasters = new();

    /// <summary>
    /// One PLC's cached master-discovery result: the confirmed candidate Net IDs, in deviceId
    /// order, and when the full sweep that found them ran, per <see cref="_timeProvider"/>.
    /// </summary>
    private sealed record CachedMasters(string[] NetIds, DateTimeOffset DiscoveredAt);

    /// <summary>
    /// How long a cached discovery is trusted before <see cref="GetMastersAsync"/> forces a full
    /// candidate sweep even though every cached master is still answering.
    ///
    /// Steady-state verification only ever re-probes Net IDs already IN the cache, so without
    /// this bound a second EtherCAT master joining an already-cached bus — or the true master
    /// reappearing at a candidate Net ID this PLC's cache doesn't hold — would never be looked
    /// for again for the life of the process; only a full sweep looks at candidates outside the
    /// cache. One minute: the caller's polling interval (<c>EtherCatOptions.PollingIntervalMs</c>)
    /// defaults to 1 s, so this bounds the periodic full sweep to roughly once every 60 poll
    /// cycles — small enough that "how stale can the reported master list get" is a number an
    /// operator can reason about, while still costing a small fraction of the every-cycle sweep
    /// this change replaces.
    /// </summary>
    internal static readonly TimeSpan FullSweepInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _timeProvider;

    // -- AMS port for master-level reads ------------------------------------------
    private const int AmsPortEcMaster = 0xFFFF;

    // -- Timeouts -----------------------------------------------------------------
    private const int DefaultTimeoutMs = 10_000;
    private const int ProbeTimeoutMs = 2_000;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(DefaultTimeoutMs);

    // -- Index groups on port 0xFFFF (EtherCAT master) ----------------------------
    // Per Beckhoff InfoSys: https://infosys.beckhoff.com/content/1033/tcsystemmanager/1089026187.html
    //
    // Master-level reads (IO = 0x0):
    private const uint IgMasterState      = 0x03; // IO=0x100: uint16 master state
    private const uint IgSlaveCount       = 0x06; // IO=0x0: uint16 projected slave count
    private const uint IgSlaveAddresses   = 0x07; // IO=0x0: uint16[] fixed addresses
    private const uint IgSlaveStates      = 0x09; // IO=0x0: all slaves {state,link} bytes
    private const uint IgFrameCounters    = 0x0C; // IO=0x0: 5×uint32 frame statistics
    private const uint IgSlaveIdentity    = 0x11; // IO=slaveAddr: 4×uint32 (vendor,product,rev,serial)
    private const uint IgCrcErrors        = 0x12; // IO=0x0: all CRC; IO=slaveAddr: 4×uint32 per port

    // Per-slave reads use IO offset = slave fixed address (NOT ADS port!)
    // e.g., IG=0x09 IO=slaveAddr → {deviceState, linkState} for that slave
    //       IG=0x11 IO=slaveAddr → identity object (4×uint32)
    //       IG=0x12 IO=slaveAddr → CRC errors per port (4×uint32)

    private const uint IoZero = 0x0;
    private const uint IoMasterState = 0x100; // IO offset for master state read (IG 0x03)

    // An ESC has at most 4 ports (A/B/C/D) and IG 0x12 reports one uint32 CRC counter per port
    // the slave actually has — so the *response length* is the port count, not a fixed 16 bytes.
    private const int MaxPortsPerSlave = 4;
    private const int BytesPerPortCounter = 4;

    private static readonly string[] PortNames = ["A", "B", "C", "D"];

    // -- CoE (CANopen over EtherCAT) ----------------------------------------------
    // SDO upload/download. Addressed with AmsPort = slave fixed address, unlike the diagnostic
    // reads above which all go to port 0xFFFF and carry the slave address as an IO offset.
    private const uint IgCoeSdo = 0xF302;

    /// <param name="logger">Logger for this client's reads and writes.</param>
    /// <param name="channels">
    /// Source of the raw ADS channels every read and write goes through. See the class doc for
    /// why the channel is fetched per call rather than cached in a field.
    /// </param>
    /// <param name="timeProvider">
    /// Clock used to timestamp and age out the master-discovery cache
    /// (<see cref="FullSweepInterval"/>). Optional so production DI wiring (<c>Program.cs</c>)
    /// is unchanged — defaults to <see cref="TimeProvider.System"/> — while tests can inject
    /// <c>FakeTimeProvider</c> to pin the periodic-resweep behaviour deterministically.
    /// </param>
    public EtherCatClient(
        ILogger<EtherCatClient> logger, IAdsRawChannelFactory channels, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _channels = channels;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // -- IEtherCatClient ----------------------------------------------------------

    /// <inheritdoc/>
    ///
    /// <remarks>
    /// Steady state costs one probe per cached master, not one per candidate
    /// <see cref="DeriveMasterCandidates"/> could name. A cached PLC re-verifies only the Net
    /// ID(s) its last successful discovery found, for up to <see cref="FullSweepInterval"/> after
    /// that discovery ran. A PLC with nothing cached yet, whose cached master(s) just failed
    /// verification, or whose cache has aged past <see cref="FullSweepInterval"/>, pays the full
    /// candidate sweep instead, in that same call.
    ///
    /// <para>
    /// <b>Staleness bound.</b> The full sweep is what rediscovers a master that moved to a
    /// different candidate Net ID (a TwinCAT project reconfigured to a different EtherCAT device
    /// number) without restarting adsify, AND what finds a second master joining an
    /// already-cached bus — steady-state verification alone only ever re-checks Net IDs already
    /// in the cache, so a brand-new one is invisible to it no matter how many times it runs. The
    /// failure-driven resweep (a cached master going silent) catches the first case immediately;
    /// it does nothing for the second, since the newcomer isn't cached and nothing cached went
    /// silent. <see cref="FullSweepInterval"/> is what bounds that gap: a new or moved master is
    /// found within <see cref="FullSweepInterval"/> of appearing even if every already-cached
    /// master keeps answering forever.
    /// </para>
    ///
    /// See <see cref="_knownMasters"/> for the cache itself.
    /// </remarks>
    public async Task<IReadOnlyList<EtherCatMasterInfo>> GetMastersAsync(
        string amsNetId, CancellationToken ct)
    {
        _logger.LogDebug("Enumerating EtherCAT masters for PLC {AmsNetId}", amsNetId);

        if (_knownMasters.TryGetValue(amsNetId, out var cached)
            && _timeProvider.GetUtcNow() - cached.DiscoveredAt < FullSweepInterval)
        {
            var verified = new List<string>(cached.NetIds.Length);
            foreach (var candidateNetId in cached.NetIds)
            {
                if (await ProbeCandidateAsync(candidateNetId, ct).ConfigureAwait(false))
                    verified.Add(candidateNetId);
            }

            // Every cached master answered again: this IS the whole call — one round trip per
            // master, not a re-sweep of every candidate DeriveMasterCandidates could name.
            if (verified.Count == cached.NetIds.Length)
                return BuildMasterInfos(verified);

            // At least one cached master went silent. Discard the WHOLE cache entry for this
            // PLC — not just the silent Net ID — and fall through to the full sweep below: a
            // master that moved to a different candidate Net ID is only found by looking at all
            // of them again, and a multi-master rack's survivors are re-confirmed the same way a
            // fresh discovery would confirm them.
            //
            // Guarded on ct: if the caller's OWN token is already cancelled, a cached probe that
            // failed with anything other than OperationCanceledException is ambiguous — a
            // genuinely silent master, or just the caller walking away mid-shutdown — and
            // discarding an otherwise-good cache on the second reading would cost the next,
            // healthy call a needless full sweep for nothing this call actually learned.
            if (!ct.IsCancellationRequested)
                _knownMasters.TryRemove(amsNetId, out _);
        }

        // Reached with no cache at all, a cache older than FullSweepInterval (the periodic
        // resweep — see the staleness-bound remarks above), or a cache just invalidated above
        // because a cached master stopped answering.
        var candidates = DeriveMasterCandidates(amsNetId);
        var found = new List<string>(candidates.Count);

        foreach (var candidateNetId in candidates)
        {
            if (await ProbeCandidateAsync(candidateNetId, ct).ConfigureAwait(false))
                found.Add(candidateNetId);
        }

        if (found.Count > 0)
        {
            // Only a genuinely answered candidate set is ever cached — see the assumed-master
            // fallback below for the reason this line does not run for it.
            _knownMasters[amsNetId] = new CachedMasters([.. found], _timeProvider.GetUtcNow());
            return BuildMasterInfos(found);
        }

        _logger.LogWarning(
            "No EtherCAT masters found for PLC {AmsNetId}. " +
            "Falling back to PLC's own AmsNetId as assumed master", amsNetId);

        // Deliberately NOT cached. This is a synthetic placeholder for "nothing answered", not a
        // discovered master — caching it would pin the PLC's own AmsNetId as a "verified" master
        // after one transient total outage. Because that Net ID is also the first candidate
        // DeriveMasterCandidates tries, a later call could then find it in steady state (one
        // probe, itself) and stop there, even after a real master reappears at a DIFFERENT
        // candidate, or a second real master joins the bus — both of which only a full sweep,
        // not a steady-state check of one Net ID, would find. Leaving this uncached means the
        // NEXT call always re-sweeps for as long as nothing has ever been genuinely discovered.
        return
        [
            new EtherCatMasterInfo
            {
                DeviceId = 0,
                Name     = "EtherCAT Master 0 (assumed)",
                AmsNetId = amsNetId,
            },
        ];
    }

    /// <summary>
    /// Probes one candidate Net ID for an EtherCAT master, answering whether it responded.
    /// Shared by <see cref="GetMastersAsync"/>'s cached-verification pass and its full-sweep
    /// pass — the only difference between the two is which Net IDs get passed here.
    /// </summary>
    private async Task<bool> ProbeCandidateAsync(string candidateNetId, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeoutMs);

            var channel = _channels.Get(candidateNetId, AmsPortEcMaster);

            // Get is total — it never proves reachability, so the probe is the ReadState
            // itself. An answer of any kind confirms the master: AdsState.Invalid on port
            // 0xFFFF IS a valid reply. An ADS error code or a timeout means "not this one".
            var state = await channel.ReadStateAsync(cts.Token).ConfigureAwait(false);

            _logger.LogInformation(
                "Found EtherCAT master at {AmsNetId} (ADS state: {State})",
                candidateNetId, state.AdsState);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Candidate {NetId} probe failed", candidateNetId);
            return false;
        }
    }

    /// <summary>
    /// Builds the <see cref="EtherCatMasterInfo"/> list for a set of confirmed master Net IDs,
    /// assigning <see cref="EtherCatMasterInfo.DeviceId"/> 0, 1, 2... in the given order. Callers
    /// pass Net IDs already in candidate order (own Net ID first, then increasing byte-5
    /// variants), so DeviceId numbering is stable across a cached call and the sweep that
    /// originally populated the cache.
    /// </summary>
    private static List<EtherCatMasterInfo> BuildMasterInfos(IReadOnlyList<string> netIds)
    {
        var masters = new List<EtherCatMasterInfo>(netIds.Count);
        for (int deviceId = 0; deviceId < netIds.Count; deviceId++)
        {
            masters.Add(new EtherCatMasterInfo
            {
                DeviceId = deviceId,
                Name     = $"EtherCAT Master {deviceId}",
                AmsNetId = netIds[deviceId],
            });
        }

        return masters;
    }

    /// <inheritdoc/>
    public async Task<EtherCatMasterState?> GetMasterStateAsync(
        string masterAmsNetId, CancellationToken ct)
    {
        _logger.LogDebug("Reading master state from {AmsNetId}:{Port}",
            masterAmsNetId, AmsPortEcMaster);

        // Read master state via IG 0x03 IO 0x100 → uint16.
        //
        // A missing or short answer means the read failed, and this returns null rather than a
        // state string for it. Substituting "Unknown" here would be indistinguishable from a
        // master that genuinely answers a raw state byte whose low nibble is 0 — the one undefined
        // code MapEcStateFromByte also renders as the bare "Unknown" (every other undefined nibble
        // becomes "Unknown(0xNN)"). The polling loop cannot recover that distinction afterwards.
        var masterStateBytes = await ReadMasterAsync(
                masterAmsNetId, IgMasterState, IoMasterState, 2, ct)
            .ConfigureAwait(false);
        if (masterStateBytes is not { Length: >= 2 })
        {
            _logger.LogWarning(
                "Master state read (IG 0x03) from {AmsNetId} failed — reporting master state as unavailable",
                masterAmsNetId);
            return null;
        }

        ushort rawState = BinaryPrimitives.ReadUInt16LittleEndian(masterStateBytes);
        string ecState = MapEcStateFromByte((byte)(rawState & 0xFF));
        _logger.LogDebug("Master state from IG 0x03: 0x{Raw:X4} → {State}", rawState, ecState);

        // Read slave count via IG 0x06 IO 0x0 → uint16. Same rule: a failed count read is not
        // "zero slaves".
        var countBytes = await ReadMasterAsync(masterAmsNetId, IgSlaveCount, IoZero, 2, ct)
            .ConfigureAwait(false);
        if (countBytes is not { Length: >= 2 })
        {
            _logger.LogWarning(
                "Slave count read (IG 0x06) from {AmsNetId} failed — reporting master state as unavailable",
                masterAmsNetId);
            return null;
        }

        return new EtherCatMasterState
        {
            CurrentState   = ecState,
            RequestedState = ecState,
            SlaveCount     = BinaryPrimitives.ReadUInt16LittleEndian(countBytes),
        };
    }

    /// <inheritdoc/>
    public async Task<FrameStatistics?> GetFrameStatisticsAsync(
        string masterAmsNetId, CancellationToken ct)
    {
        _logger.LogDebug("Reading frame statistics from {AmsNetId}", masterAmsNetId);

        // IG 0x0C IO 0x0: 5×uint32 frame counters.
        //
        // The whole 20 bytes or nothing. IAdsRawChannel.ReadAsync contracts that a SHORT read is
        // possible, and a short block here used to be parsed field-by-field with a 0 substituted
        // for every counter the answer did not reach: an 8-byte answer served a real
        // cyclicSendFrames next to a fabricated cyclicLostFrames: 0, with incompleteReads empty
        // saying nothing was missing. The next cycle then computed queuedFramesPerSecond as a
        // delta against that zero and spiked. There is no partial frame reading — the block is one
        // reading of five counters, so anything short of it is a failed read.
        const int FrameCounterBlockBytes = 5 * 4;

        var buffer = await ReadMasterAsync(
                masterAmsNetId, IgFrameCounters, IoZero, FrameCounterBlockBytes, ct)
            .ConfigureAwait(false);

        if (buffer is null || buffer.Length < FrameCounterBlockBytes)
        {
            _logger.LogWarning(
                "Frame counter read (IG 0x0C) from {AmsNetId} answered {Bytes} of {Expected} bytes " +
                "— reporting frame statistics as unavailable",
                masterAmsNetId, buffer?.Length ?? 0, FrameCounterBlockBytes);
            return null;
        }

        return ParseFrameStatistics(buffer);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EtherCatSlaveInfo>?> GetConfiguredSlavesAsync(
        string masterAmsNetId, CancellationToken ct)
    {
        _logger.LogDebug("Reading configured slaves from {AmsNetId}", masterAmsNetId);

        // 1. Get slave count via IG 0x06 IO 0x0. A failed read is not "zero slaves" — the caller
        //    needs those two cases apart, so it answers null here and [] only for a real zero.
        var countBytes = await ReadMasterAsync(masterAmsNetId, IgSlaveCount, IoZero, 2, ct)
            .ConfigureAwait(false);
        if (countBytes is not { Length: >= 2 })
        {
            _logger.LogWarning(
                "Slave count read (IG 0x06) from {AmsNetId} failed — reporting the slave list as unavailable",
                masterAmsNetId);
            return null;
        }

        int slaveCount = BinaryPrimitives.ReadUInt16LittleEndian(countBytes);
        _logger.LogDebug("Master reports {Count} configured slave(s)", slaveCount);
        if (slaveCount == 0)
            return [];

        // 2. Get slave fixed addresses via IG 0x07 IO 0x0 (uint16[]).
        //
        //    Nothing is synthesised when this fails or comes back short. Filling in 1..N produced
        //    a full-looking slave list at addresses that do not exist on the bus, and filling the
        //    tail of a short answer with zeros produced duplicate addresses. Both shapes reach
        //    change detection as "every real slave disappeared".
        var addrBytes = await ReadMasterAsync(
                masterAmsNetId, IgSlaveAddresses, IoZero, slaveCount * 2, ct)
            .ConfigureAwait(false);
        if (addrBytes is null || addrBytes.Length < slaveCount * 2)
        {
            _logger.LogWarning(
                "Slave address read (IG 0x07) from {AmsNetId} answered {Bytes} of {Expected} bytes " +
                "— reporting the slave list as unavailable",
                masterAmsNetId, addrBytes?.Length ?? 0, slaveCount * 2);
            return null;
        }

        var addresses = new ushort[slaveCount];
        for (int i = 0; i < slaveCount; i++)
            addresses[i] = BinaryPrimitives.ReadUInt16LittleEndian(addrBytes.AsSpan(i * 2));

        // 3. Get all slave states via IG 0x09 IO 0x0 (2 bytes per slave: state + link).
        //    A missing or short answer would leave the uncovered slaves at deviceState/linkState 0,
        //    which decodes to "Unknown", not present and disabled — a fabricated bus-wide fault.
        var allStates = await ReadMasterAsync(
                masterAmsNetId, IgSlaveStates, IoZero, slaveCount * 2, ct)
            .ConfigureAwait(false);
        if (allStates is null || allStates.Length < slaveCount * 2)
        {
            _logger.LogWarning(
                "Slave state read (IG 0x09) from {AmsNetId} answered {Bytes} of {Expected} bytes " +
                "— reporting the slave list as unavailable",
                masterAmsNetId, allStates?.Length ?? 0, slaveCount * 2);
            return null;
        }

        // 4. Read identities for all slaves (IG 0x11 IO=slaveAddr) to get name/type
        //    and build slave info list. Unlike the reads above, a failed identity read is not
        //    treated as unavailability: it costs a name and a type, neither of which change
        //    detection compares, and the slave's address and state are already known.
        var slaves = new List<EtherCatSlaveInfo>(slaveCount);
        for (int i = 0; i < slaveCount; i++)
        {
            byte deviceState = allStates[i * 2];
            byte linkState = allStates[i * 2 + 1];

            string ecState = MapEcStateFromByte(deviceState);
            bool isPresent = linkState != 0 || deviceState != 0;
            bool hasError = (deviceState & 0x10) != 0;

            // Read identity to derive device name/type
            string slaveName = $"Slave {addresses[i]}";
            string slaveType = "Unknown";
            var identityBytes = await ReadMasterAsync(
                    masterAmsNetId, IgSlaveIdentity, addresses[i], 16, ct)
                .ConfigureAwait(false);

            if (identityBytes is { Length: >= 8 })
            {
                uint vendorId = BinaryPrimitives.ReadUInt32LittleEndian(identityBytes);
                uint productCode = BinaryPrimitives.ReadUInt32LittleEndian(identityBytes.AsSpan(4));
                slaveType = BeckhoffDeviceDecoder.DecodeDeviceType(vendorId, productCode);
                slaveName = slaveType != "Unknown" ? slaveType : slaveName;
            }

            slaves.Add(new EtherCatSlaveInfo
            {
                PhysicalAddress      = addresses[i],
                AutoIncrementAddress = (ushort)i,
                Name                 = slaveName,
                Type                 = slaveType,
                CurrentState         = ecState,
                RequestedState       = ecState,
                IsPresent            = isPresent,
                HasError             = hasError,
                IsDisabled           = deviceState == 0 && linkState == 0,
            });
        }

        return slaves;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EtherCatScannedSlave>?> GetScannedSlavesAsync(
        string masterAmsNetId, CancellationToken ct)
    {
        _logger.LogDebug("Reading scanned slave identities from {AmsNetId}", masterAmsNetId);

        // First get slave addresses. Same contract as GetConfiguredSlavesAsync: null when a read
        // this list depends on failed, [] only when the master reports zero slaves.
        var countBytes = await ReadMasterAsync(masterAmsNetId, IgSlaveCount, IoZero, 2, ct)
            .ConfigureAwait(false);
        if (countBytes is not { Length: >= 2 })
        {
            _logger.LogWarning(
                "Slave count read (IG 0x06) from {AmsNetId} failed — reporting scanned identities as unavailable",
                masterAmsNetId);
            return null;
        }

        int slaveCount = BinaryPrimitives.ReadUInt16LittleEndian(countBytes);
        if (slaveCount == 0)
            return [];

        var addrBytes = await ReadMasterAsync(
                masterAmsNetId, IgSlaveAddresses, IoZero, slaveCount * 2, ct)
            .ConfigureAwait(false);
        if (addrBytes is null || addrBytes.Length < slaveCount * 2)
        {
            _logger.LogWarning(
                "Slave address read (IG 0x07) from {AmsNetId} answered {Bytes} of {Expected} bytes " +
                "— reporting scanned identities as unavailable",
                masterAmsNetId, addrBytes?.Length ?? 0, slaveCount * 2);
            return null;
        }

        var slaves = new List<EtherCatScannedSlave>(slaveCount);

        for (int i = 0; i < slaveCount; i++)
        {
            ushort addr = BinaryPrimitives.ReadUInt16LittleEndian(addrBytes.AsSpan(i * 2));

            // Per-slave identity: IG 0x11 IO=slaveAddr on port 0xFFFF
            // Returns 4×uint32: vendor, product, revision, serial
            //
            // A failed read here does not drop the slave from the list, and does not zero-fill
            // its identity either — both would fabricate a reading. The address is already known
            // from IG 0x07 above, so the slave stays listed at that address with its identity
            // fields null, distinct from an address IG 0x07 never reported at all (which means
            // this slave is not physically on the bus — see EtherCatScannedSlave's class doc).
            uint? vendorId = null, productCode = null, revisionNo = null, serialNo = null;

            var identityBytes = await ReadMasterAsync(
                    masterAmsNetId, IgSlaveIdentity, addr, 16, ct)
                .ConfigureAwait(false);

            if (identityBytes is { Length: >= 16 })
            {
                vendorId    = BinaryPrimitives.ReadUInt32LittleEndian(identityBytes);
                productCode = BinaryPrimitives.ReadUInt32LittleEndian(identityBytes.AsSpan(4));
                revisionNo  = BinaryPrimitives.ReadUInt32LittleEndian(identityBytes.AsSpan(8));
                serialNo    = BinaryPrimitives.ReadUInt32LittleEndian(identityBytes.AsSpan(12));

                _logger.LogDebug(
                    "Slave {Addr} identity: vendor=0x{Vendor:X8}, product=0x{Product:X8}, rev=0x{Rev:X8}, serial=0x{Serial:X8}",
                    addr, vendorId, productCode, revisionNo, serialNo);
            }
            else
            {
                _logger.LogWarning(
                    "Slave identity read (IG 0x11) for {Addr} on {AmsNetId} answered {Bytes} of 16 bytes " +
                    "— reporting this slave's scanned identity as unavailable",
                    addr, masterAmsNetId, identityBytes?.Length ?? 0);
            }

            slaves.Add(new EtherCatScannedSlave
            {
                PhysicalAddress = addr,
                VendorId        = vendorId,
                ProductCode     = productCode,
                RevisionNumber  = revisionNo,
                SerialNumber    = serialNo,
            });
        }

        return slaves;
    }

    /// <inheritdoc/>
    public async Task<EtherCatSlaveDetail?> GetSlaveDetailAsync(
        string masterAmsNetId, ushort physicalAddress, CancellationToken ct)
    {
        _logger.LogDebug("Reading slave detail for addr {Addr} from {AmsNetId}",
            physicalAddress, masterAmsNetId);

        // Whole-call granularity: any of these three failing answers null for all of them. A
        // partial object here would put zeroed identity fields and an empty port list in front of
        // a caller, which is indistinguishable from a genuine wrong-device fault on a healthy rack.
        var identityBytes = await ReadMasterAsync(
                masterAmsNetId, IgSlaveIdentity, physicalAddress, 16, ct)
            .ConfigureAwait(false);
        if (identityBytes is not { Length: >= 16 })
        {
            _logger.LogWarning(
                "Slave identity read (IG 0x11) for {Addr} on {AmsNetId} answered {Bytes} of 16 bytes " +
                "— reporting slave detail as unavailable",
                physicalAddress, masterAmsNetId, identityBytes?.Length ?? 0);
            return null;
        }

        var slaveStateBytes = await ReadMasterAsync(
                masterAmsNetId, IgSlaveStates, physicalAddress, 2, ct)
            .ConfigureAwait(false);
        if (slaveStateBytes is not { Length: >= 1 })
        {
            _logger.LogWarning(
                "Slave state read (IG 0x09) for {Addr} on {AmsNetId} failed " +
                "— reporting slave detail as unavailable",
                physicalAddress, masterAmsNetId);
            return null;
        }

        // IG 0x12 IO=slaveAddr → one uint32 CRC counter per linked port. A block shorter than one
        // counter is a failed read, not a slave with no ports — every slave on the bus has at
        // least its upstream port.
        var portBytes = await ReadMasterAsync(
                masterAmsNetId, IgCrcErrors, physicalAddress, MaxPortsPerSlave * BytesPerPortCounter, ct)
            .ConfigureAwait(false);
        if (CountReportedPorts(portBytes) == 0)
        {
            _logger.LogWarning(
                "Slave port read (IG 0x12) for {Addr} on {AmsNetId} answered {Bytes} bytes " +
                "— reporting slave detail as unavailable",
                physicalAddress, masterAmsNetId, portBytes?.Length ?? 0);
            return null;
        }

        uint cfgVendor   = BinaryPrimitives.ReadUInt32LittleEndian(identityBytes);
        uint cfgProduct  = BinaryPrimitives.ReadUInt32LittleEndian(identityBytes.AsSpan(4));
        uint cfgRevision = BinaryPrimitives.ReadUInt32LittleEndian(identityBytes.AsSpan(8));
        uint cfgSerial   = BinaryPrimitives.ReadUInt32LittleEndian(identityBytes.AsSpan(12));

        return new EtherCatSlaveDetail
        {
            ConfiguredVendorId       = cfgVendor,
            ConfiguredProductCode    = cfgProduct,
            ConfiguredRevisionNumber = cfgRevision,
            ConfiguredSerialNumber   = cfgSerial,
            // Known limitation: scanned identity is set to configured identity because
            // reading the actual scanned (bus-level) identity would require a different
            // ADS read mechanism (e.g., ESC register access or EoE mailbox queries)
            // that is not available through the standard EtherCAT master ADS interface.
            ScannedVendorId          = cfgVendor,
            ScannedProductCode       = cfgProduct,
            ScannedRevisionNumber    = cfgRevision,
            ScannedSerialNumber      = cfgSerial,
            IdentityMatch            = true,
            InitError                = (slaveStateBytes[0] & 0x10) != 0,
            Ports                    = BuildPortInfo(portBytes),
        };
    }

    /// <inheritdoc/>
    public async Task<SlaveErrorCounters?> GetSlaveErrorCountersAsync(
        string masterAmsNetId, ushort physicalAddress, CancellationToken ct)
    {
        _logger.LogDebug("Reading error counters for slave {Addr}", physicalAddress);

        // IG 0x12 IO=slaveAddr → one uint32 CRC counter per linked port. Only emit the ports the
        // master actually reported, so this list lines up with the detail read's port list rather
        // than padding every slave out to a fixed four ports that may not exist.
        var crcBytes = await ReadMasterAsync(
                masterAmsNetId, IgCrcErrors, physicalAddress, MaxPortsPerSlave * BytesPerPortCounter, ct)
            .ConfigureAwait(false);

        int linkedPorts = CountReportedPorts(crcBytes);
        if (linkedPorts == 0)
        {
            // Not "a slave with no ports" — no such slave is on the bus. This is the read failing.
            _logger.LogWarning(
                "CRC counter read (IG 0x12) for {Addr} on {AmsNetId} answered {Bytes} bytes " +
                "— reporting error counters as unavailable",
                physicalAddress, masterAmsNetId, crcBytes?.Length ?? 0);
            return null;
        }

        var portCounters = new List<PortErrorCounters>(linkedPorts);

        for (int i = 0; i < linkedPorts; i++)
        {
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes!.AsSpan(i * BytesPerPortCounter));

            portCounters.Add(new PortErrorCounters
            {
                Port               = PortNames[i],
                CrcErrors          = (int)crc,
                // CONSTANTS, NOT READINGS — see AbnormalStateChanges below.
                ForwardedCrcErrors = 0,
                LostLinkCount      = 0,
            });
        }

        return new SlaveErrorCounters
        {
            PhysicalAddress      = physicalAddress,
            // CONSTANT, NOT A READING, and the same is true of the two per-port fields above. The
            // IG 0x12 block is one uint32 CRC counter per linked port and nothing else — it carries
            // no abnormal-state-change count, no forwarded-CRC count and no lost-link count, and
            // adsify reads no other index group that would supply them. All three are fixed 0 on
            // every response and no bus event will move them.
            //
            // They are therefore NOT covered by this branch's "a field fed by a read that did not
            // answer is null" guarantee: no read feeds them, so there is nothing to be absent, and
            // they never appear in EtherCatReads/incompleteReads either. An operator watching a
            // flapping link for connectionLosses to rise will wait forever — which is why they are
            // named as constants in docs/site/content/docs/api/ethercat.md under "Fields adsify
            // never reads", in the same terms as the Sync Units gap, rather than left to look like
            // a healthy reading of zero.
            AbnormalStateChanges = 0,
            Ports                = portCounters,
        };
    }

    /// <inheritdoc/>
    public async Task<bool> ResetSlaveErrorCountersAsync(
        string masterAmsNetId, ushort physicalAddress, CancellationToken ct)
    {
        _logger.LogInformation("Resetting error counters for slave {Addr} on {AmsNetId}",
            physicalAddress, masterAmsNetId);

        // Write zeros to IG 0x12 IO=slaveAddr — the same (group, offset) pair
        // GetSlaveErrorCountersAsync reads this slave's CRC counters from, so the reset is
        // scoped to the addressed slave.
        //
        // This deliberately does NOT touch IG 0x0C IO 0x0. That is the master's *global* frame
        // counter block: writing it wipes cyclic/queued send and lost counts for the whole
        // master, which a per-slave endpoint has no business doing. Verified against a
        // CX/EK1100 bus — a single call there reset cyclicSendFrames from 343675 to 0.
        //
        // The master returns 4 bytes per port the slave actually has (8 for a two-port
        // terminal, 4 for the last device in a chain), so size the write to the block it
        // reports rather than assuming a fixed port count.
        var current = await ReadMasterAsync(
                masterAmsNetId, IgCrcErrors, physicalAddress, MaxPortsPerSlave * BytesPerPortCounter, ct)
            .ConfigureAwait(false);
        int width = current is { Length: >= BytesPerPortCounter }
            ? current.Length
            : BytesPerPortCounter;

        try
        {
            var channel = _channels.Get(masterAmsNetId, AmsPortEcMaster);
            var zeros = new byte[width];

            await channel
                .WriteAsync(IgCrcErrors, physicalAddress, zeros.AsMemory(), DefaultTimeout, ct)
                .ConfigureAwait(false);

            _logger.LogDebug("Cleared {Width}b of CRC counters for slave {Addr}",
                width, physicalAddress);
            return true;
        }
        catch (AdsErrorException ex)
        {
            _logger.LogWarning("Reset of slave {Addr} counters failed: {Error}",
                physicalAddress, ex.ErrorCode);
            return false;
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Reset of slave {Addr} counters timed out", physicalAddress);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Error resetting counters for slave {Addr} on {AmsNetId}",
                physicalAddress, masterAmsNetId);
            return false;
        }
    }

    /// <summary>
    /// Not implemented — always returns an empty list, and <c>EtherCatService</c> reports the
    /// endpoint as unimplemented rather than passing the empty list off as "no sync units".
    ///
    /// A sweep of index groups 0x01-0x60 on master port 0xFFFF against a live EK1100 bus found
    /// these responders beyond the ones this class already uses, none of which could be
    /// attributed to sync units:
    ///
    ///   IG 0x22  512 bytes, record-structured, starts with slave addresses — looks like a
    ///            port/connection table rather than sync unit grouping
    ///   IG 0x45  uint16, 0
    ///   IG 0x48  uint32, 2                      — plausibly a count, but of what is unconfirmed
    ///   IG 0x51  4 bytes per slave, also readable at IO=slaveAddr; on an 8-slave bus the
    ///            records were {1..8, 2, 3} — the leading value is a per-slave index, not a
    ///            repeated group id, so it is not sync unit membership
    ///   IG 0x53  16 bytes, mostly zero, carrying one slave address (the DC-capable EL7047)
    ///   IG 0x5B  uint32, a large constant
    ///   IG 0x5D  uint32 per slave, all 8 — duplicates the state already read via IG 0x09
    ///
    /// Every candidate reads as zero or uniform on a healthy bus, so an interpretation could not
    /// be validated: the fields the API would expose (HasError, FaultCounter, FramesMissed) are
    /// exactly the ones that only become non-zero under a fault. Guessing here would put
    /// fabricated diagnostics in front of callers, which is the failure this feature has already
    /// been bitten by once. Implementing this needs either Beckhoff's documentation for the
    /// group or a bus fault to observe.
    /// </summary>
    public Task<IReadOnlyList<SyncUnitInfo>> GetSyncUnitsAsync(
        string masterAmsNetId, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<SyncUnitInfo>>([]);
    }

    /// <inheritdoc/>
    public async Task<CoeReadResult> ReadCoeObjectAsync(
        string masterAmsNetId,
        ushort physicalAddress,
        ushort index,
        byte subIndex,
        int timeoutMs,
        int maxBytes,
        CancellationToken ct)
    {
        _logger.LogDebug("CoE read 0x{Index:X4}:{Sub:X2} from slave {Addr} on {AmsNetId}",
            index, subIndex, physicalAddress, masterAmsNetId);

        try
        {
            // NOTE the addressing: AmsPort is the slave's fixed address, NOT 0xFFFF. Port 0xFFFF
            // answers IG 0xF302 from the *master's own* object dictionary (verified: it returns
            // vendor 2 / revision 1857 and cannot serve 0x1018:02 or 0x1008), so routing CoE
            // through the diagnostic port would silently read the wrong device.
            var channel = _channels.Get(masterAmsNetId, physicalAddress);
            var memory = new Memory<byte>(new byte[maxBytes]);

            int read = await channel
                .ReadAsync(IgCoeSdo, CoeOffset(index, subIndex), memory,
                    TimeSpan.FromMilliseconds(timeoutMs), ct)
                .ConfigureAwait(false);

            _logger.LogDebug("CoE read 0x{Index:X4}:{Sub:X2} returned {Bytes}b",
                index, subIndex, read);

            return new CoeReadResult
            {
                Succeeded = true,
                Data = memory[..read].ToArray(),
            };
        }
        catch (AdsErrorException ex)
        {
            _logger.LogDebug("CoE read 0x{Index:X4}:{Sub:X2} from slave {Addr} answered {Error}",
                index, subIndex, physicalAddress, ex.ErrorCode);

            return new CoeReadResult
            {
                Succeeded = false,
                Data = [],
                Reason = Classify(ex.ErrorCode),
                Error = ex.ErrorCode.ToString(),
            };
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("CoE read 0x{Index:X4}:{Sub:X2} from slave {Addr} timed out",
                index, subIndex, physicalAddress);

            return new CoeReadResult
            {
                Succeeded = false, Data = [], Reason = CoeFailureReason.NoMailbox, Error = "Timeout",
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "CoE read 0x{Index:X4}:{Sub:X2} from slave {Addr} threw",
                index, subIndex, physicalAddress);

            return new CoeReadResult
            {
                Succeeded = false, Data = [], Reason = CoeFailureReason.AdsError, Error = ex.GetType().Name,
            };
        }
    }

    /// <summary>
    /// Classifies a CoE read failure. Observed on a live EK1100 bus: a mailbox-less terminal
    /// answers PortNotConnected once the router knows the port is absent and simply times out
    /// before that, while a slave that does have a mailbox rejects an unknown object with
    /// DeviceInvalidOffset.
    /// </summary>
    internal static CoeFailureReason Classify(AdsErrorCode error) => error switch
    {
        AdsErrorCode.PortNotConnected or AdsErrorCode.TargetPortNotFound
            or AdsErrorCode.ClientSyncTimeOut or AdsErrorCode.DeviceTimeOut => CoeFailureReason.NoMailbox,
        AdsErrorCode.DeviceInvalidOffset => CoeFailureReason.ObjectNotFound,
        _ => CoeFailureReason.AdsError,
    };

    /// <summary>
    /// Beckhoff's ADS-to-CoE offset encoding: object index in the high 16 bits, subindex in the
    /// low 8. Confirmed on hardware — 0x1018:02 read this way matches the product code the
    /// diagnostic interface reports via IG 0x11 for the same slave.
    /// </summary>
    internal static uint CoeOffset(ushort index, byte subIndex) => ((uint)index << 16) | subIndex;

    // -- Private helpers ----------------------------------------------------------

    /// <summary>
    /// Reads data from the EtherCAT master (port 0xFFFF).
    /// Per-slave reads use IO offset = slave fixed address (all on port 0xFFFF).
    /// </summary>
    private async Task<byte[]?> ReadMasterAsync(
        string masterAmsNetId, uint ig, uint io, int size, CancellationToken ct)
    {
        return await ReadRawAsync(masterAmsNetId, AmsPortEcMaster, ig, io, size, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a raw index-group/offset block, answering <see langword="null"/> when the device
    /// declines.
    ///
    /// What a caller does with that null is deliberately split.
    /// <see cref="GetMasterStateAsync"/> and <see cref="GetConfiguredSlavesAsync"/> PROPAGATE it —
    /// they answer null themselves, and the polling loop reports the master's diagnostics as
    /// degraded — because the master-level reads they are built from (IG 0x03, and IG 0x06/0x07/0x09
    /// at IO 0) decide the identity and state of every slave in the snapshot that change detection
    /// compares. Reads that only decorate a slave already known by address and state fall back to a
    /// default instead: the identity read (IG 0x11), the per-slave counter block (IG 0x12) and the
    /// per-slave state read (IG 0x09 at IO=slaveAddr, used by <see cref="GetSlaveDetailAsync"/>).
    /// Losing a device name must not blank out a whole rack's diagnostics. Substituting a value for
    /// a failure in the first group is what made a dropped read indistinguishable from a bus event.
    ///
    /// The catch set below is narrow by design, not an oversight left over from a wider
    /// catch-all: <see cref="AdsErrorException"/>, <see cref="TimeoutException"/> and
    /// <see cref="AdsConnectionUnavailableException"/> are exactly the three answers this call
    /// site treats as "the device declined, not a real fault". Anything else propagates — and
    /// every caller of the <c>ReadMasterAsync</c>/<c>ReadRawAsync</c> chain sits inside
    /// <c>EtherCatMonitor.PollMasterAsync</c>'s own per-field
    /// <c>catch (Exception ex) when (ex is not OperationCanceledException)</c>, so an
    /// unrecognised exception still degrades just that one field for the cycle — one level up,
    /// tagged with which read it was, rather than being folded silently into this method's
    /// generic null-on-failure return.
    ///
    /// Connection lifetime, retry and the per-attempt timeout belong to
    /// <see cref="IAdsRawChannel"/>. Note what that changes: an ADS error code is an *answer*
    /// there — never retried, never a torn-down channel — so probing a mailbox-less slave costs
    /// one round trip rather than the two this method used to spend retrying PortNotConnected.
    ///
    /// The channel is fetched per call and never disposed: <see cref="IAdsRawChannelFactory.Get"/>
    /// is total and cached, and idle eviction drops the transport rather than the facade.
    ///
    /// The <see cref="AdsErrorException"/> and <see cref="TimeoutException"/> arms log at
    /// Warning, not Debug: at the shipped Serilog default (<c>Serilog:MinimumLevel:Default =
    /// Information</c>, see <c>appsettings.json</c>) a Debug line here is never written at all,
    /// which made this class's entire degradation story invisible in production — see the class
    /// doc's "Reliability" remarks. <see cref="AdsConnectionUnavailableException"/> stays at
    /// Debug; only the other two arms were raised.
    /// </summary>
    private async Task<byte[]?> ReadRawAsync(
        string amsNetId, int port, uint ig, uint io, int size, CancellationToken ct)
    {
        try
        {
            var channel = _channels.Get(amsNetId, port);
            var memory = new Memory<byte>(new byte[size]);

            int read = await channel
                .ReadAsync(ig, io, memory, DefaultTimeout, ct)
                .ConfigureAwait(false);

            if (read > 0)
            {
                _logger.LogDebug("Read {NetId}:{Port} IG=0x{IG:X2}: {Bytes}b",
                    amsNetId, port, ig, read);
                return memory[..read].ToArray();
            }

            _logger.LogDebug("Read {NetId}:{Port} IG=0x{IG:X2} returned no data",
                amsNetId, port, ig);
            return null;
        }
        catch (AdsErrorException ex)
        {
            // Warning, not Debug: this is the failure this class degrades silently on, and at the
            // shipped Serilog default (MinimumLevel:Default = Information) a Debug line here is
            // never written at all — see the class doc's "Reliability" remarks.
            _logger.LogWarning("Read {NetId}:{Port} IG=0x{IG:X2} answered {Err}",
                amsNetId, port, ig, ex.ErrorCode);
            return null;
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Read {NetId}:{Port} IG=0x{IG:X2} timed out", amsNetId, port, ig);
            return null;
        }
        catch (AdsConnectionUnavailableException)
        {
            _logger.LogDebug("Read {NetId}:{Port} IG=0x{IG:X2}: channel unavailable",
                amsNetId, port, ig);
            return null;
        }
    }

    /// <summary>
    /// Maps an IG 0x12 per-slave counter block onto the four ESC ports. Ports the master
    /// reported a counter for are linked; the rest are absent. A block that is missing or
    /// shorter than one counter means the read failed — the port state is then unknown
    /// rather than "no ports", so the caller gets the unconfigured default.
    /// </summary>
    internal static IReadOnlyList<SlavePortInfo> BuildPortInfo(byte[]? counterBlock)
    {
        int linkedPorts = CountReportedPorts(counterBlock);
        if (linkedPorts == 0)
            return CreateDefaultPorts();

        var ports = new List<SlavePortInfo>(MaxPortsPerSlave);
        for (int i = 0; i < MaxPortsPerSlave; i++)
        {
            bool linked = i < linkedPorts;
            ports.Add(new SlavePortInfo
            {
                Port = PortNames[i],
                // The master's ADS interface does not expose the port medium, so a linked port
                // is reported as EBus. Consequence: a bus with cable-connected branches
                // (EK1122 junction, EP box) is rendered as one continuous trace instead of
                // separate traces joined by cable links.
                Physic     = linked ? "EBus" : "none",
                Configured = linked,
                LinkState  = linked,
            });
        }

        return ports;
    }

    /// <summary>
    /// Number of ports the master reported a counter for, capped at the ESC maximum.
    /// </summary>
    internal static int CountReportedPorts(byte[]? counterBlock) =>
        counterBlock is null
            ? 0
            : Math.Min(counterBlock.Length / BytesPerPortCounter, MaxPortsPerSlave);

    private static IReadOnlyList<SlavePortInfo> CreateDefaultPorts() =>
    [
        new() { Port = "A", Physic = "none", Configured = false, LinkState = false },
        new() { Port = "B", Physic = "none", Configured = false, LinkState = false },
        new() { Port = "C", Physic = "none", Configured = false, LinkState = false },
        new() { Port = "D", Physic = "none", Configured = false, LinkState = false },
    ];

    /// <summary>
    /// Derives candidate EtherCAT master AMS NetIds from the PLC's AMS NetId.
    /// Tries PLC's own NetId first, then byte-5 variants 2..5.
    /// </summary>
    private static List<string> DeriveMasterCandidates(string plcAmsNetId)
    {
        var candidates = new List<string> { plcAmsNetId };

        var parts = plcAmsNetId.Split('.');
        if (parts.Length != 6)
            return candidates;

        for (int deviceId = 2; deviceId <= 5; deviceId++)
        {
            parts[4] = deviceId.ToString();
            var candidate = string.Join(".", parts);
            if (candidate != plcAmsNetId)
                candidates.Add(candidate);
        }

        return candidates;
    }

    private static string MapEcStateFromAds(AdsState adsState) => adsState switch
    {
        AdsState.Init     => "Init",
        AdsState.Reset    => "PreOp",
        AdsState.Config   => "SafeOp",
        AdsState.Run      => "Op",
        AdsState.Reconfig => "Boot",
        AdsState.Stop     => "Stop",
        _                 => adsState.ToString(),
    };

    /// <summary>
    /// Maps EtherCAT state byte (from IG 0x09) to state string.
    /// Encoding per ETG.1000: bits 0-3 = state, bit 4 = error flag.
    /// </summary>
    private static string MapEcStateFromByte(byte stateByte) => (stateByte & 0x0F) switch
    {
        0x01 => "Init",
        0x02 => "PreOp",
        0x03 => "Bootstrap",
        0x04 => "SafeOp",
        0x08 => "Op",
        0x00 => "Unknown",
        _    => $"Unknown(0x{stateByte:X2})",
    };

    /// <summary>
    /// Parses IG 0x0C frame counter data (20 bytes = 5 × uint32).
    /// Per Beckhoff InfoSys ADS Interface documentation, the layout is:
    ///   [0] System time (lower 32 bits of DC time, 100ns resolution)
    ///   [1] Cyclic frames sent
    ///   [2] Lost cyclic frames
    ///   [3] Acyclic frames sent
    ///   [4] Lost acyclic frames
    ///
    /// <para>
    /// Precondition: <paramref name="buffer"/> is the FULL 20 bytes —
    /// <see cref="GetFrameStatisticsAsync"/> rejects anything shorter as a failed read. There is
    /// deliberately no length handling and no catch-all here: both existed once, and both answered
    /// a fully-populated object of zeros for a block the master never sent, which reads downstream
    /// as a genuinely idle master rather than as a read that did not answer.
    /// </para>
    /// </summary>
    private static FrameStatistics ParseFrameStatistics(byte[] buffer) =>
        // Field 0 is system time (not a frame counter), skip it
        new()
        {
            CyclicSendFrames    = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)),
            CyclicLostFrames    = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8)),
            QueuedSendFrames    = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12)),
            QueuedLostFrames    = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16)),
            // Per-second rates are calculated by the polling service (delta / interval).
            // Null, not 0: this method has no previous reading to delta against.
            CyclicFramesPerSecond = null,
            QueuedFramesPerSecond = null,
            // CONSTANTS, NOT READINGS. IG 0x0C carries five uint32s and none of them is a Tx/Rx
            // error count; adsify reads no other index group that would supply one. These two are
            // fixed 0 on every response and nothing on any bus will move them — documented as such
            // under "Fields adsify never reads" in docs/site/content/docs/api/ethercat.md. They are
            // NOT covered by the branch's null-means-absent guarantee, because there is no read
            // behind them to be absent.
            CyclicTxRxErrors    = 0,
            QueuedTxRxErrors    = 0,
        };
}
