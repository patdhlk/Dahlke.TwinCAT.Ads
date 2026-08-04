using Dahlke.TwinCAT.Ads;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dahlke.EtherCAT.Diagnostics;

internal sealed class EtherCatMonitor(
    IEtherCatClient client,
    IEtherCatCache cache,
    IEtherCatDiagnosticsHandler handler,
    IEtherCatOptionsSource optionsSource,
    IOptions<TwinCatAdsOptions> adsOptions,
    ILogger<EtherCatMonitor> logger,
    TimeProvider? timeProvider = null) : BackgroundService, IEtherCatMonitor
{
    /// <summary>
    /// Clock for the poll cycle budget. Optional so production wiring is unchanged — defaults to
    /// <see cref="TimeProvider.System"/> — while tests inject <c>FakeTimeProvider</c> to drive
    /// budget expiry without sleeping. Same pattern as <c>EtherCatClient</c>'s discovery cache.
    /// </summary>
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private readonly Dictionary<string, PlcTargetOptions> _targets = adsOptions.Value.Targets;
    private readonly HashSet<(int masterId, ushort address, string port)> _crcNotified = [];

    /// <summary>
    /// Reason carried by <see cref="MasterDiagnosticsDegradedEvent"/> when
    /// <see cref="IEtherCatClient.GetMasterStateAsync"/> could not answer. Deliberately names the
    /// missing RESULT, not one read: that call depends on two reads — the master state (IG 0x03)
    /// and the slave count (IG 0x06) — and either failing produces this reason.
    /// </summary>
    internal const string MasterStateUnavailable = "master state unavailable";

    /// <summary>
    /// Reason carried by <see cref="MasterDiagnosticsDegradedEvent"/> when
    /// <see cref="IEtherCatClient.GetConfiguredSlavesAsync"/> could not answer — its slave count
    /// (IG 0x06), fixed address (IG 0x07) or slave state (IG 0x09) read failed or answered short.
    ///
    /// In practice a failing IG 0x06 surfaces as <see cref="MasterStateUnavailable"/> instead,
    /// because <see cref="PollMasterAsync"/> reads the master state first and returns on it; this
    /// reason is reached when IG 0x07 or IG 0x09 fails, or when IG 0x06 answered for the master
    /// state read and then failed for this one.
    /// </summary>
    internal const string SlaveListUnavailable = "configured slave list unavailable";

    /// <summary>
    /// Reason carried by <see cref="MasterDiagnosticsDegradedEvent"/> when a cycle ran past
    /// <see cref="EtherCatOptions.PollCycleBudgetMs"/> and was abandoned. Distinct from the two
    /// read-failure reasons: those say a specific read did not answer, this says adsify ran out of
    /// time before it had a complete reading — which may be several slow reads rather than any one
    /// failed one.
    /// </summary>
    internal const string CycleBudgetExceeded = "poll cycle budget exceeded";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // No explicit wait for the embedded ADS router here: Dahlke.TwinCAT.Ads keeps its own
        // router-ready signal internal (it gates the library's connection pool, not the raw ADS
        // channels this client uses), so there is no public surface to await. EtherCatClient gets
        // an IAdsRawChannel per call and every call site below already tolerates connection
        // failures (caught, logged, retried next cycle), so polling starts immediately and simply
        // degrades gracefully — logging warnings — until the router comes up.
        logger.LogInformation("EtherCAT diagnostics monitor starting for {PlcCount} PLC(s)", _targets.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            var minInterval = int.MaxValue;

            foreach (var (plcId, plcOptions) in _targets)
            {
                var etherCatOptions = optionsSource.For(plcId);
                if (etherCatOptions is null)
                {
                    logger.LogDebug("PLC {PlcId} has no EtherCat configuration, skipping", plcId);
                    continue;
                }

                if (etherCatOptions.PollingIntervalMs < minInterval)
                    minInterval = etherCatOptions.PollingIntervalMs;

                try
                {
                    await PollPlcAsync(plcId, plcOptions, etherCatOptions, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error polling EtherCAT data for PLC {PlcId}", plcId);
                }
            }

            var delay = minInterval == int.MaxValue ? 1000 : minInterval;
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task PollPlcAsync(
        string plcId,
        PlcTargetOptions plcOptions,
        EtherCatOptions etherCatOptions,
        CancellationToken ct)
    {
        IReadOnlyList<EtherCatMasterInfo> masters;
        try
        {
            masters = await client.GetMastersAsync(plcOptions.AmsNetId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enumerate EtherCAT masters for PLC {PlcId}", plcId);
            return;
        }

        foreach (var master in masters)
        {
            try
            {
                await PollMasterAsync(plcId, master, etherCatOptions, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error polling master {MasterId} ({MasterName}) on PLC {PlcId}",
                    master.DeviceId, master.Name, plcId);
            }
        }
    }

    /// <summary>
    /// Polls one master once, under a wall-clock budget. Internal rather than private so a test can
    /// drive exactly one cycle: the degradation contract is about what a SEQUENCE of cycles emits
    /// (once on transition, not once per cycle), which cannot be pinned through
    /// <see cref="ExecuteAsync"/>'s timer loop.
    ///
    /// <para>
    /// The budget is enforced two ways and both are load-bearing.
    /// </para>
    /// <para>
    /// <b>The linked token</b> bounds a read ALREADY IN FLIGHT. Without it one read still burns its
    /// full <c>RawChannels:TimeoutMs</c> × (<c>RetryCount</c> + 1) — 2 × 10 s on the shipped
    /// defaults — before anything notices, making the real bound the budget plus 20 s. Measured
    /// against Dahlke.TwinCAT.Ads 0.8.0: an uncancelled hung read costs 20 013 ms, the same read
    /// under a token that fires mid-flight costs 202 ms.
    /// </para>
    /// <para>
    /// <b>The <see cref="CycleBudget"/> checks between reads</b> are what abandon a cycle that
    /// overran WITHOUT any read being cancelled — a read that answers just after the deadline
    /// throws nothing, so the loop would otherwise walk the remaining slaves and store a snapshot
    /// built long past the budget. They also cover the case where a cancelled read is reported as a
    /// null rather than an exception: <c>EtherCatClient.ReadRawAsync</c> swallows
    /// <see cref="TimeoutException"/> into a null, which one level up reads as a failed DECORATING
    /// read and is not fatal, so the loop would run to completion storing a snapshot with every
    /// per-slave field null. That second case does not arise on 0.8.0 —
    /// <c>AdsRawChannel.RunAttemptsAsync</c> rethrows <see cref="OperationCanceledException"/>
    /// carrying the caller's token when the caller's token is what fired, and only maps to
    /// <see cref="TimeoutException"/> when the per-attempt timeout fired alone — but it is a
    /// library-internal detail this class must not depend on.
    /// </para>
    /// <para>
    /// <b>The bound is soft by about one read, deliberately.</b> Checking BETWEEN reads bounds a
    /// cycle to the budget plus however long the read in progress takes to unwind, and an overrun
    /// that lands on the LAST slave's read is not caught at all: the loop exits normally and the
    /// snapshot is stored despite having been built past the budget. There is no check between the
    /// loop and <c>cache.Update</c>, and there should not be. That reading has a COMPLETE slave
    /// list, so it fabricates no presence events, and anything that did not answer is named in
    /// <see cref="EtherCatSnapshot.IncompleteReads"/> — throwing away a complete reading because it
    /// arrived 50 ms late would lose real data to buy nothing. What the budget guarantees is not an
    /// exact deadline but the two things #43 needed: a cycle cannot run on indefinitely, and an
    /// INCOMPLETE reading is never stored.
    /// </para>
    /// </summary>
    internal async Task PollMasterAsync(
        string plcId,
        EtherCatMasterInfo master,
        EtherCatOptions etherCatOptions,
        CancellationToken ct)
    {
        // Read before anything in this cycle can change it: EnterDegradedAsync needs the marker's
        // value from the START of the cycle to emit on transition rather than once per cycle.
        var wasDegraded = cache.IsDegraded(plcId, master.DeviceId);

        // A budget that cannot bound anything is a misconfiguration, and the worst thing to do with
        // it is let it throw. CancellationTokenSource rejects any delay below -1 ms, and that
        // ArgumentOutOfRangeException would be raised BEFORE the try below, escape to PollPlcAsync's
        // generic handler, and be logged and stepped over — leaving the master un-degraded with a
        // frozen snapshot reporting diagnosticsDegraded: false. That is precisely the silent
        // staleness #43 exists to remove, reachable through a typo in #43's own knob.
        //
        // The guard covers the whole non-positive range rather than only the values that throw. 0
        // and -1 are survivable today by accident — 0 fires the deadline immediately, and -1 is
        // Timeout.InfiniteTimeSpan to CancellationTokenSource but still satisfies CycleBudget's
        // `elapsed >= limit` — and both already degrade on the first between-reads check. Handling
        // them here makes one rule out of three coincidences, and reports the same way.
        if (etherCatOptions.PollCycleBudgetMs <= 0)
        {
            // Transition-gated for the same reason EnterDegradedAsync gates its own Warning: a
            // misconfigured budget is a standing condition, not an event, and a master polled every
            // second must not restate it every second.
            if (!wasDegraded)
            {
                logger.LogWarning(
                    "EtherCAT PollCycleBudgetMs for PLC {PlcId} is {BudgetMs} ms, which cannot bound a " +
                    "poll cycle — master {MasterId} ({MasterName}) will be reported degraded until it " +
                    "is set to a positive value",
                    plcId, etherCatOptions.PollCycleBudgetMs, master.DeviceId, master.Name);
            }

            await EnterDegradedAsync(
                plcId, master, etherCatOptions, wasDegraded, CycleBudgetExceeded, ct);
            return;
        }

        var limit = TimeSpan.FromMilliseconds(etherCatOptions.PollCycleBudgetMs);

        using var budgetCts = new CancellationTokenSource(limit, _timeProvider);
        using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(ct, budgetCts.Token);

        var budget = new CycleBudget(_timeProvider, _timeProvider.GetTimestamp(), limit);

        try
        {
            await PollMasterCoreAsync(plcId, master, etherCatOptions, budget, cycleCts.Token, ct);
        }
        catch (OperationCanceledException)
            when (!ct.IsCancellationRequested && budgetCts.IsCancellationRequested)
        {
            // Transition-gated, matching EnterDegradedAsync's own Warning and the other overrun
            // route. Both routes report the same operator-visible condition, so they must cost the
            // same to watch: ungated, an overrun that keeps arriving as a cancelled in-flight read
            // would emit one Warning per poll interval — one a second on the defaults — where an
            // overrun caught by a between-reads check emits one, ever.
            if (!wasDegraded)
            {
                logger.LogWarning(
                    "EtherCAT poll cycle for master {MasterId} ({MasterName}) on PLC {PlcId} exceeded " +
                    "its {BudgetMs} ms budget and was abandoned — raise PollCycleBudgetMs if this rack " +
                    "legitimately needs longer",
                    master.DeviceId, master.Name, plcId, etherCatOptions.PollCycleBudgetMs);
            }

            // Note ct, not cycleCts.Token: EnterDegradedAsync hands the event to the configured
            // handler, and handing it the token that just fired would kill the notification with
            // the budget.
            await EnterDegradedAsync(
                plcId, master, etherCatOptions, wasDegraded, CycleBudgetExceeded, ct);
        }
    }

    /// <summary>
    /// One cycle's reads, in order. <paramref name="cycleToken"/> is the budget-bounded token every
    /// <see cref="IEtherCatClient"/> call runs under; <paramref name="ct"/> is the caller's own and
    /// is what every notification is delivered under, so one outlives the budget that provoked it.
    /// </summary>
    private async Task PollMasterCoreAsync(
        string plcId,
        EtherCatMasterInfo master,
        EtherCatOptions etherCatOptions,
        CycleBudget budget,
        CancellationToken cycleToken,
        CancellationToken ct)
    {
        // Read both before anything in this cycle can change them.
        //
        // previousSnapshot is the LAST KNOWN-GOOD reading, not merely the previous cycle's: a
        // degraded cycle returns below without calling cache.Update, so the cache never holds a
        // snapshot built from a failed read. That is what lets a real state change that straddles
        // a dropped read still be detected — Op, dropped read, SafeOp compares SafeOp against Op.
        var previousSnapshot = cache.GetSnapshot(plcId, master.DeviceId);
        var wasDegraded = cache.IsDegraded(plcId, master.DeviceId);

        // The two reads change detection depends on answer null when they fail, distinctly from
        // any value they could report. Neither is substituted for: a master whose state cannot be
        // read has no state here, and a slave list that could not be read is not an empty bus.
        EtherCatMasterState? masterState = null;
        try { masterState = await client.GetMasterStateAsync(master.AmsNetId, cycleToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { logger.LogWarning(ex, "Failed to read master state for {MasterId} on {PlcId}", master.DeviceId, plcId); }

        if (masterState is null)
        {
            await EnterDegradedAsync(plcId, master, etherCatOptions, wasDegraded, MasterStateUnavailable, ct);
            return;
        }

        IReadOnlyList<EtherCatSlaveInfo>? configuredSlaves = null;
        try { configuredSlaves = await client.GetConfiguredSlavesAsync(master.AmsNetId, cycleToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { logger.LogWarning(ex, "Failed to read configured slaves for master {MasterId}", master.DeviceId); }

        if (configuredSlaves is null)
        {
            await EnterDegradedAsync(plcId, master, etherCatOptions, wasDegraded, SlaveListUnavailable, ct);
            return;
        }

        // Both gating reads answered but the cycle is already over budget. Abandon rather than
        // spend more of it on reads that only decorate the snapshot.
        if (budget.Exhausted)
        {
            await EnterDegradedAsync(
                plcId, master, etherCatOptions, wasDegraded, CycleBudgetExceeded, ct);
            return;
        }

        // The rest decorate the snapshot without feeding change detection, so a failure here does
        // not degrade the master — adsify still read its state and every slave's state, so the
        // snapshot is fresh and its events are real. What it does do is RECORD the failure, so the
        // fields that read feeds can be reported absent rather than filled in. See EtherCatReads.
        var incompleteReads = EtherCatReads.None;

        FrameStatistics? frameStats = null;
        IReadOnlyList<EtherCatScannedSlave>? scannedSlaves = null;
        IReadOnlyList<SyncUnitInfo> syncUnits = [];

        try { frameStats = await client.GetFrameStatisticsAsync(master.AmsNetId, cycleToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { logger.LogWarning(ex, "Failed to read frame statistics for master {MasterId}", master.DeviceId); }

        if (frameStats is null)
            incompleteReads |= EtherCatReads.FrameStatistics;

        try { scannedSlaves = await client.GetScannedSlavesAsync(master.AmsNetId, cycleToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { logger.LogWarning(ex, "Failed to read scanned slaves for master {MasterId}", master.DeviceId); }

        if (scannedSlaves is null)
            incompleteReads |= EtherCatReads.ScannedIdentities;

        try { syncUnits = await client.GetSyncUnitsAsync(master.AmsNetId, cycleToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { logger.LogWarning(ex, "Failed to read sync units for master {MasterId}", master.DeviceId); }

        // Duplicate-tolerant: two slaves configured to the same fixed address is a bus
        // misconfiguration, but ToDictionary would throw for it and abort the whole cycle.
        var scannedLookup = new Dictionary<ushort, EtherCatScannedSlave>();
        foreach (var scannedSlave in scannedSlaves ?? [])
            scannedLookup.TryAdd(scannedSlave.PhysicalAddress, scannedSlave);

        var slaveSnapshots = new List<EtherCatSlaveSnapshot>();
        foreach (var slave in configuredSlaves)
        {
            // Abandon rather than store a truncated slave list: a short list reaches change
            // detection as "the rest of the rack disappeared" and fabricates presence events.
            if (budget.Exhausted)
            {
                await EnterDegradedAsync(
                    plcId, master, etherCatOptions, wasDegraded, CycleBudgetExceeded, ct);
                return;
            }

            EtherCatSlaveDetail? detail = null;
            SlaveErrorCounters? errors = null;

            try
            {
                detail = await client.GetSlaveDetailAsync(master.AmsNetId, slave.PhysicalAddress, cycleToken);
                errors = await client.GetSlaveErrorCountersAsync(master.AmsNetId, slave.PhysicalAddress, cycleToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to read detail/errors for slave {Address} on master {MasterId}",
                    slave.PhysicalAddress, master.DeviceId);
            }

            // One slave's read failing flags the whole snapshot: a consumer finds WHICH slaves by
            // looking for the nulls. Flagging per slave would duplicate that on every entry.
            if (detail is null)
                incompleteReads |= EtherCatReads.SlaveDetail;
            if (errors is null)
                incompleteReads |= EtherCatReads.SlaveErrorCounters;

            scannedLookup.TryGetValue(slave.PhysicalAddress, out var scanned);

            // An entry that IS in the scan but carries no identity means THIS slave's IG 0x11 read
            // failed within an otherwise-successful scan — flag it exactly like SlaveDetail and
            // SlaveErrorCounters above (at least one slave's read of this kind did not answer this
            // cycle). No entry at all is a different, unflagged fact: that address genuinely was
            // not in what the master reported (see EtherCatReads.ScannedIdentities and
            // EtherCatSlaveSnapshot.Scanned for the three-way disambiguation this preserves).
            // Checked against all four fields, not just VendorId: EtherCatClient only ever sets
            // them null together, but EtherCatScannedSlave is publicly constructible and that
            // invariant is documented, not enforced by the type. EtherCatService's DTO mapping
            // already requires all four non-null before it will build a SlaveIdentityDto, so a
            // producer that set some but not all would otherwise reach the wire as an unexplained
            // scannedIdentity: null with this flag clear — exactly what the flag exists to avoid.
            if (scanned is { VendorId: null } or { ProductCode: null } or { RevisionNumber: null } or { SerialNumber: null })
                incompleteReads |= EtherCatReads.ScannedIdentities;

            slaveSnapshots.Add(new EtherCatSlaveSnapshot
            {
                PhysicalAddress = slave.PhysicalAddress,
                AutoIncrementAddress = slave.AutoIncrementAddress,
                Name = slave.Name,
                Type = slave.Type,
                CurrentState = slave.CurrentState,
                RequestedState = slave.RequestedState,
                IsPresent = slave.IsPresent,
                HasError = slave.HasError,
                IsDisabled = slave.IsDisabled,
                Detail = detail,
                ErrorCounters = errors,
                Scanned = scanned,
            });
        }

        // Per-second rates from the delta with the last known-good snapshot. Null when this cycle
        // has no counters, or when the previous known-good cycle had none to delta against.
        var enrichedStats = CalculateFrameRates(frameStats, previousSnapshot);

        var snapshot = new EtherCatSnapshot
        {
            PlcId = plcId,
            MasterAmsNetId = master.AmsNetId,
            MasterDeviceId = master.DeviceId,
            MasterName = master.Name,
            Timestamp = DateTimeOffset.UtcNow,
            MasterState = masterState,
            FrameStatistics = enrichedStats,
            IncompleteReads = incompleteReads,
            Slaves = slaveSnapshots,
            SyncUnits = syncUnits,
        };

        // Known-good: this also clears any degraded marker, so the next cycle compares against
        // this reading.
        cache.Update(plcId, master.DeviceId, snapshot);

        if (wasDegraded)
        {
            logger.LogInformation(
                "EtherCAT diagnostics for master {MasterId} ({MasterName}) on PLC {PlcId} are readable again",
                master.DeviceId, master.Name, plcId);
        }

        if (!etherCatOptions.EnableNotifications)
            return;

        // Recovery first, then whatever actually changed while the reads were down — a subscriber
        // sees "diagnostics are back" before the state change that recovery revealed.
        if (wasDegraded)
        {
            await SendNotificationAsync(plcId, master.DeviceId, new MasterDiagnosticsDegradedEvent
            {
                MasterId = master.DeviceId,
                MasterName = master.Name,
                Degraded = false,
            }, ct);
        }

        var events = DetectChanges(previousSnapshot, snapshot, etherCatOptions.CrcErrorThreshold, _crcNotified);
        foreach (var evt in events)
        {
            await SendNotificationAsync(plcId, master.DeviceId, evt, ct);
        }
    }

    /// <summary>
    /// Records that this cycle could not read the master's diagnostics, and tells subscribers once.
    ///
    /// The stored snapshot is deliberately left alone: it stays the last known-good reading, so
    /// the next successful cycle compares against what the master last actually said rather than
    /// against a placeholder. <paramref name="wasDegraded"/> is the marker's value from the START
    /// of this cycle, which is what makes this emit on transition rather than once per cycle — a
    /// master polled every second while unreachable produces one event, not one per second.
    ///
    /// <para>
    /// <b>Load-bearing dependency.</b> This only ever runs for a master <c>PollPlcAsync</c> was
    /// given, and under a TOTAL bus loss every candidate probe in
    /// <see cref="IEtherCatClient.GetMastersAsync"/> fails — so the only reason any master is
    /// polled at all, and therefore the only reason an outage is reported rather than silent, is
    /// that <c>EtherCatClient.GetMastersAsync</c> falls back to an assumed master 0 when its
    /// enumeration finds none. Removing that fallback would make a total bus loss silent again by
    /// a different route.
    /// </para>
    /// <para>
    /// The same fallback bounds what this can report: because enumeration collapses to that single
    /// assumed master, masters 1..N are not polled during a total bus loss and keep reporting the
    /// last <c>diagnosticsDegraded</c> value they had. Pre-existing enumeration behaviour, tracked
    /// separately — see the spec's §7.
    /// </para>
    /// </summary>
    private async Task EnterDegradedAsync(
        string plcId,
        EtherCatMasterInfo master,
        EtherCatOptions etherCatOptions,
        bool wasDegraded,
        string reason,
        CancellationToken ct)
    {
        cache.MarkDegraded(plcId, master.DeviceId);

        if (wasDegraded)
            return;

        logger.LogWarning(
            "EtherCAT diagnostics for master {MasterId} ({MasterName}) on PLC {PlcId} are unavailable: {Reason}",
            master.DeviceId, master.Name, plcId, reason);

        if (!etherCatOptions.EnableNotifications)
            return;

        await SendNotificationAsync(plcId, master.DeviceId, new MasterDiagnosticsDegradedEvent
        {
            MasterId = master.DeviceId,
            MasterName = master.Name,
            Degraded = true,
            Reason = reason,
        }, ct);
    }

    /// <summary>
    /// Hands one event to the configured handler. Every failure is swallowed and logged: losing a
    /// notification is far better than a misbehaving handler stopping the poll loop, and the loop
    /// is what keeps the REST snapshot current.
    /// </summary>
    private async Task SendNotificationAsync(string plcId, int masterId, IEtherCatEvent evt, CancellationToken ct)
    {
        try
        {
            await handler.HandleAsync(plcId, evt, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deliver {EventName} notification for PLC {PlcId} master {MasterId}",
                evt.GetType().Name, plcId, masterId);
        }
    }

    /// <summary>
    /// Calculates per-second frame rates by comparing current counters with the previous snapshot.
    /// Returns a new FrameStatistics with the raw counters preserved and rates filled in.
    ///
    /// <para>
    /// Three ways the rates come back null, all of them "not derivable" rather than "zero":
    /// this cycle had no counter read, the last known-good cycle had none to delta against, or no
    /// measurable time has passed. Reporting 0 for any of them puts a frame rate in front of an
    /// operator that no counter ever produced.
    /// </para>
    /// </summary>
    private static FrameStatistics? CalculateFrameRates(
        FrameStatistics? current,
        EtherCatSnapshot? previous)
    {
        if (current is null)
            return null;

        var prev = previous?.FrameStatistics;
        if (prev is null)
            return current;

        double intervalSec = (DateTimeOffset.UtcNow - previous!.Timestamp).TotalSeconds;
        if (intervalSec <= 0)
            return current;

        double cyclicPerSec = Math.Max(0, (current.CyclicSendFrames - prev.CyclicSendFrames) / intervalSec);
        double queuedPerSec = Math.Max(0, (current.QueuedSendFrames - prev.QueuedSendFrames) / intervalSec);

        return new FrameStatistics
        {
            CyclicSendFrames      = current.CyclicSendFrames,
            QueuedSendFrames      = current.QueuedSendFrames,
            CyclicLostFrames      = current.CyclicLostFrames,
            QueuedLostFrames      = current.QueuedLostFrames,
            CyclicFramesPerSecond = Math.Round(cyclicPerSec, 1),
            QueuedFramesPerSecond = Math.Round(queuedPerSec, 1),
            CyclicTxRxErrors      = current.CyclicTxRxErrors,
            QueuedTxRxErrors      = current.QueuedTxRxErrors,
        };
    }

    /// <summary>
    /// Clears the CRC notification tracking for a specific slave, allowing re-notification after
    /// <see cref="IEtherCatClient.ResetSlaveErrorCountersAsync"/> has reset its counters.
    /// </summary>
    public void ClearCrcNotification(int masterId, ushort address)
    {
        _crcNotified.RemoveWhere(entry => entry.masterId == masterId && entry.address == address);
    }

    /// <summary>Whether a CRC threshold event has already been emitted for this port.</summary>
    internal bool IsCrcNotified(int masterId, ushort address, string port) =>
        _crcNotified.Contains((masterId, address, port));

    /// <summary>Records a CRC threshold event as already emitted, suppressing re-notification.</summary>
    internal void MarkCrcNotified(int masterId, ushort address, string port) =>
        _crcNotified.Add((masterId, address, port));

    /// <summary>
    /// Compares two snapshots and returns a list of notification events for any detected changes.
    ///
    /// <para>
    /// Both arguments are readings the master actually answered. <c>PollMasterAsync</c> never
    /// builds a snapshot from a failed read — it returns early and marks the master degraded — and
    /// the cache only ever stores snapshots passed to <c>IEtherCatCache.Update</c>, so
    /// <paramref name="previous"/> is the last known-good reading. This method therefore does no
    /// sentinel inference: every difference it sees is a difference the master reported, and it
    /// reports all of them. That includes a master answering <c>"Unknown"</c> (raw state nibble 0)
    /// or <c>"Unknown(0xNN)"</c> (any other nibble outside ETG.1000's 1/2/3/4/8), and a master
    /// answering zero configured slaves — all genuine readings here rather than read failures in
    /// disguise.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<IEtherCatEvent> DetectChanges(
        EtherCatSnapshot? previous,
        EtherCatSnapshot current,
        int crcThreshold,
        HashSet<(int masterId, ushort address, string port)> crcNotified)
    {
        if (previous is null)
            return [];

        var events = new List<IEtherCatEvent>();
        var masterId = current.MasterDeviceId;

        // Master state changes.
        if (previous.MasterState.CurrentState != current.MasterState.CurrentState
            || previous.MasterState.RequestedState != current.MasterState.RequestedState)
        {
            events.Add(new MasterStateChangedEvent
            {
                MasterId = masterId,
                MasterName = current.MasterName,
                CurrentState = current.MasterState.CurrentState,
                PreviousState = previous.MasterState.CurrentState,
                RequestedState = current.MasterState.RequestedState,
            });
        }

        // Build lookups for both slave lists by physical address
        var previousSlaves = ByAddress(previous.Slaves);
        var currentSlaves = ByAddress(current.Slaves);

        // Check current slaves against previous
        foreach (var slave in current.Slaves)
        {
            if (!previousSlaves.TryGetValue(slave.PhysicalAddress, out var prevSlave))
                continue; // New slave, no diff to emit

            // Slave state changed
            if (prevSlave.CurrentState != slave.CurrentState || prevSlave.HasError != slave.HasError)
            {
                events.Add(new SlaveStateChangedEvent
                {
                    MasterId = masterId,
                    Address = slave.PhysicalAddress,
                    Name = slave.Name,
                    CurrentState = slave.CurrentState,
                    PreviousState = prevSlave.CurrentState,
                    HasError = slave.HasError,
                });
            }

            // Slave presence changed
            if (prevSlave.IsPresent != slave.IsPresent)
            {
                events.Add(new SlavePresenceChangedEvent
                {
                    MasterId = masterId,
                    Address = slave.PhysicalAddress,
                    Name = slave.Name,
                    IsPresent = slave.IsPresent,
                });
            }

            // CRC error threshold
            DetectCrcErrors(events, masterId, slave, crcThreshold, crcNotified);
        }

        // Disappeared slaves (in previous but not in current). No guard on the current list going
        // empty: a slave list only reaches this method when the master answered, so an empty one
        // means the master reports zero configured slaves.
        //
        // Note the asymmetry, which this change does not alter: a slave that appears in current
        // but not in previous emits nothing (see the `continue` above). Disappearance is reported,
        // appearance is not.
        foreach (var prevSlave in previous.Slaves)
        {
            if (!currentSlaves.ContainsKey(prevSlave.PhysicalAddress))
            {
                events.Add(new SlavePresenceChangedEvent
                {
                    MasterId = masterId,
                    Address = prevSlave.PhysicalAddress,
                    Name = prevSlave.Name,
                    IsPresent = false,
                });
            }
        }

        // Sync unit faults. Duplicate-tolerant for the same reason as ByAddress: a repeated id
        // would throw ArgumentException out of this method and abort the master's poll cycle.
        // Unreachable today — GetSyncUnitsAsync returns [] by design — but the shape is the one
        // that bit this class already, so it is not left as the odd one out.
        var previousSyncUnits = new Dictionary<int, SyncUnitInfo>(
            previous.SyncUnits.Count);
        foreach (var prevSyncUnit in previous.SyncUnits)
            previousSyncUnits.TryAdd(prevSyncUnit.Id, prevSyncUnit);
        foreach (var syncUnit in current.SyncUnits)
        {
            if (previousSyncUnits.TryGetValue(syncUnit.Id, out var prevSu)
                && prevSu.HasError != syncUnit.HasError)
            {
                events.Add(new SyncUnitFaultEvent
                {
                    MasterId = masterId,
                    SyncUnitId = syncUnit.Id,
                    HasError = syncUnit.HasError,
                    FaultCounter = syncUnit.FaultCounter,
                });
            }
        }

        return events;
    }

    /// <summary>
    /// Indexes slaves by fixed address, keeping the first entry when an address repeats.
    ///
    /// Two slaves sharing a fixed address is a bus misconfiguration, but it must not throw out of
    /// change detection: <c>ToDictionary</c> raises <see cref="ArgumentException"/> for a duplicate
    /// key, which escapes <c>DetectChanges</c> and aborts the whole poll cycle for that master.
    /// </summary>
    private static Dictionary<ushort, EtherCatSlaveSnapshot> ByAddress(
        IReadOnlyList<EtherCatSlaveSnapshot> slaves)
    {
        var lookup = new Dictionary<ushort, EtherCatSlaveSnapshot>(slaves.Count);
        foreach (var slave in slaves)
            lookup.TryAdd(slave.PhysicalAddress, slave);
        return lookup;
    }

    private static void DetectCrcErrors(
        List<IEtherCatEvent> events,
        int masterId,
        EtherCatSlaveSnapshot slave,
        int crcThreshold,
        HashSet<(int masterId, ushort address, string port)> crcNotified)
    {
        // No counters means the read did not answer, not that every port is clean. Emitting
        // nothing is right, and so is leaving crcNotified alone: an alarm that already fired must
        // not re-arm just because adsify briefly lost sight of the counter behind it.
        if (slave.ErrorCounters is null)
            return;

        foreach (var portError in slave.ErrorCounters.Ports)
        {
            if (portError.CrcErrors >= crcThreshold)
            {
                var key = (masterId, slave.PhysicalAddress, portError.Port);
                if (crcNotified.Add(key))
                {
                    events.Add(new CrcErrorThresholdExceededEvent
                    {
                        MasterId = masterId,
                        Address = slave.PhysicalAddress,
                        Name = slave.Name,
                        Port = portError.Port,
                        CrcCount = portError.CrcErrors,
                        Threshold = crcThreshold,
                    });
                }
            }
        }
    }

    /// <summary>
    /// One cycle's wall-clock allowance. Checked between reads; the deadline that bounds a read
    /// already in flight is the linked <see cref="CancellationTokenSource"/> in
    /// <see cref="PollMasterAsync"/>.
    /// </summary>
    private readonly record struct CycleBudget(TimeProvider Clock, long StartedAt, TimeSpan Limit)
    {
        internal bool Exhausted => Clock.GetElapsedTime(StartedAt) >= Limit;
    }
}
