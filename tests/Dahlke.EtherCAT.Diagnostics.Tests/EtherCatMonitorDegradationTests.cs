using Dahlke.EtherCAT.Diagnostics;
using Dahlke.TwinCAT.Ads;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Dahlke.EtherCAT.Diagnostics.Tests;

/// <summary>
/// Drives <see cref="EtherCatMonitor.PollMasterAsync"/> cycle by cycle against a scripted
/// <see cref="IEtherCatClient"/>, pinning what a subscriber sees when reads fail and recover.
///
/// These are the invariants <see cref="EtherCatMonitorTests"/> cannot reach: they are about
/// a SEQUENCE of cycles (emit once on transition, never rebaseline on a failed read), not about a
/// single snapshot comparison.
/// </summary>
public class EtherCatMonitorDegradationTests
{
    private const string PlcId = "plc1";
    private const int MasterId = 0;
    private const string MasterNetId = "5.80.192.39.3.1";
    private const string MasterName = "EtherCAT Master 0";

    // A second master on the same PLC. Degradation is keyed by (plcId, masterDeviceId), and
    // Degradation_is_scoped_to_one_master_and_does_not_silence_its_siblings is what holds that
    // keying in place — every other test here would still pass against a single monitor-wide flag.
    private const int SecondMasterId = 1;
    private const string SecondMasterNetId = "5.80.192.39.4.1";
    private const string SecondMasterName = "EtherCAT Master 1";

    private readonly IEtherCatClient _client = Substitute.For<IEtherCatClient>();
    private readonly EtherCatCache _cache = new();

    /// <summary>
    /// Where notifications land. Substituting the handler rather than a hub proxy is what makes the
    /// assertions here name the event TYPE instead of a stringly-typed transport method, and it
    /// counts each event once — the monitor calls the handler once per event, and it is the
    /// application's handler that decides how many groups that fans out to.
    /// </summary>
    private readonly IEtherCatDiagnosticsHandler _handler = Substitute.For<IEtherCatDiagnosticsHandler>();

    private readonly IEtherCatOptionsSource _optionsSource = Substitute.For<IEtherCatOptionsSource>();

    /// <summary>
    /// Drives the poll cycle budget. A scripted read advances this clock instead of sleeping, so a
    /// cycle can overrun a 5 s budget in a test that runs in microseconds.
    /// </summary>
    private readonly FakeTimeProvider _clock = new();

    /// <summary>
    /// Captures what the monitor logged. Two contracts here are log-only — a misconfigured budget
    /// has to name the option and the value, and both overrun routes have to log on the TRANSITION
    /// rather than once per cycle — and a NullLogger would leave either free to regress.
    /// </summary>
    private readonly CapturingLoggerProvider _logs = new();

    private readonly EtherCatMonitor _sut;

    private readonly EtherCatMasterInfo _master = new()
    {
        DeviceId = MasterId,
        Name = MasterName,
        AmsNetId = MasterNetId,
    };

    private readonly EtherCatMasterInfo _secondMaster = new()
    {
        DeviceId = SecondMasterId,
        Name = SecondMasterName,
        AmsNetId = SecondMasterNetId,
    };

    private readonly EtherCatOptions _options = new() { EnableNotifications = true, CrcErrorThreshold = 100 };

    public EtherCatMonitorDegradationTests()
    {
        // Never consulted by these tests — they drive PollMasterAsync directly and hand it the
        // options, rather than going through ExecuteAsync's per-PLC lookup — but configured
        // coherently so the fixture cannot be read as "this PLC has no EtherCAT configuration".
        _optionsSource.For(PlcId).Returns(_options);

        _sut = new EtherCatMonitor(
            _client,
            _cache,
            _handler,
            _optionsSource,
            Options.Create(new TwinCatAdsOptions()),
            new LoggerFactory([_logs]).CreateLogger<EtherCatMonitor>(),
            _clock);

        // Reads that decorate the snapshot without feeding change detection. Fixed for every test
        // here (and for both masters) so the scripted master-state and slave-list reads are the
        // only variables.
        _client.GetFrameStatisticsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FrameStatistics());
        _client.GetScannedSlavesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EtherCatScannedSlave>?>([]));
        _client.GetSyncUnitsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SyncUnitInfo>>([]));
        _client.GetSlaveDetailAsync(Arg.Any<string>(), Arg.Any<ushort>(), Arg.Any<CancellationToken>())
            .Returns(SlaveDetail());
        _client.GetSlaveErrorCountersAsync(Arg.Any<string>(), Arg.Any<ushort>(), Arg.Any<CancellationToken>())
            .Returns(ci => new SlaveErrorCounters
            {
                PhysicalAddress = ci.ArgAt<ushort>(1),
                AbnormalStateChanges = 0,
                Ports = [],
            });
    }

    // -- scripting helpers --------------------------------------------------------

    /// <summary>Scripts the next cycle as a master that answers <paramref name="state"/> with
    /// <paramref name="slaves"/> configured. Masters are scripted independently, keyed by Net ID.</summary>
    private void MasterAnswers(string netId, string state, params ushort[] slaves)
    {
        _client.GetMasterStateAsync(netId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<EtherCatMasterState?>(new EtherCatMasterState
            {
                CurrentState = state,
                RequestedState = state,
                SlaveCount = slaves.Length,
            }));

        _client.GetConfiguredSlavesAsync(netId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EtherCatSlaveInfo>?>(
                slaves.Select(SlaveInfo).ToList()));
    }

    /// <summary>Scripts the default master's next cycle.</summary>
    private void MasterAnswers(string state, params ushort[] slaves) =>
        MasterAnswers(MasterNetId, state, slaves);

    /// <summary>Scripts the next cycle as a failed master-state read.</summary>
    private void MasterStateReadFails(string netId = MasterNetId) =>
        _client.GetMasterStateAsync(netId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<EtherCatMasterState?>(null));

    /// <summary>Scripts the next cycle as a failed configured-slave-list read.</summary>
    private void SlaveListReadFails(string netId = MasterNetId) =>
        _client.GetConfiguredSlavesAsync(netId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EtherCatSlaveInfo>?>(null));

    private Task PollOnce() => _sut.PollMasterAsync(PlcId, _master, _options, CancellationToken.None);

    private Task PollSecondMasterOnce() =>
        _sut.PollMasterAsync(PlcId, _secondMaster, _options, CancellationToken.None);

    /// <summary>Every event handed to the handler so far, in order.</summary>
    private List<IEtherCatEvent> Emitted() =>
        HandleCalls().Select(call => (IEtherCatEvent)call.GetArguments()[1]!).ToList();

    private IEnumerable<NSubstitute.Core.ICall> HandleCalls() =>
        _handler.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IEtherCatDiagnosticsHandler.HandleAsync));

    /// <summary>Every Warning the monitor logged so far, formatted, in order.</summary>
    private List<string> Warnings() =>
        _logs.Entries.Where(entry => entry.Level == LogLevel.Warning)
            .Select(entry => entry.Message)
            .ToList();

    /// <summary>The type names of the events handed to the handler, in order.</summary>
    private List<string> EmittedNames() =>
        Emitted().Select(evt => evt.GetType().Name).ToList();

    private static EtherCatSlaveInfo SlaveInfo(ushort address) => new()
    {
        PhysicalAddress = address,
        AutoIncrementAddress = 0,
        Name = $"Slave {address}",
        Type = "EL1008",
        CurrentState = "Op",
        RequestedState = "Op",
        IsPresent = true,
        HasError = false,
        IsDisabled = false,
    };

    private static EtherCatSlaveDetail SlaveDetail() => new()
    {
        ConfiguredVendorId = 2,
        ConfiguredProductCode = 1,
        ConfiguredRevisionNumber = 1,
        ConfiguredSerialNumber = 0,
        ScannedVendorId = 2,
        ScannedProductCode = 1,
        ScannedRevisionNumber = 1,
        ScannedSerialNumber = 0,
        IdentityMatch = true,
        InitError = false,
        Ports = [],
    };

    // -- invariants ---------------------------------------------------------------

    // A dropped read must not be silent. Before this change GetMasterStateAsync answered a
    // non-null state carrying "Unknown", so PollMasterAsync's `masterState is null` guard never
    // fired for a dropped read and the sentinel guards swallowed the rest: TwinCAT leaving Run
    // produced no notification at all.
    [Fact]
    public async Task A_failed_master_state_read_emits_a_degraded_event()
    {
        MasterAnswers("Op", 1001, 1002);
        await PollOnce();

        MasterStateReadFails();
        await PollOnce();

        Emitted().Should().ContainSingle()
            .Which.Should().BeOfType<MasterDiagnosticsDegradedEvent>()
            .Which.Should().BeEquivalentTo(new
            {
                MasterId,
                MasterName,
                Degraded = true,
                Reason = EtherCatMonitor.MasterStateUnavailable,
            });
    }

    [Fact]
    public async Task A_failed_slave_list_read_emits_a_degraded_event_and_no_presence_events()
    {
        MasterAnswers("Op", 1001, 1002, 1003, 1004, 1005, 1006, 1007, 1008);
        await PollOnce();

        SlaveListReadFails();
        await PollOnce();

        Emitted().Should().ContainSingle()
            .Which.Should().BeOfType<MasterDiagnosticsDegradedEvent>()
            .Which.Reason.Should().Be(EtherCatMonitor.SlaveListUnavailable);
        Emitted().OfType<SlavePresenceChangedEvent>().Should().BeEmpty(
            "a read that did not answer observed no slave leaving the bus");
    }

    // A rack polling at 1 s must not produce a notification per second while its master is
    // unreachable — the event marks the transition, not the condition.
    [Fact]
    public async Task Repeated_degraded_cycles_emit_one_event_not_one_per_cycle()
    {
        MasterAnswers("Op", 1001);
        await PollOnce();

        MasterStateReadFails();
        for (int cycle = 0; cycle < 10; cycle++)
            await PollOnce();

        Emitted().OfType<MasterDiagnosticsDegradedEvent>().Should().ContainSingle();
    }

    // The invariant the old design could not hold: Op → dropped read → SafeOp. The dropped read
    // used to be cached as an "Unknown" baseline, so the genuine SafeOp was compared against
    // Unknown, suppressed, and never reported again because SafeOp == SafeOp forever after.
    [Fact]
    public async Task A_state_change_straddling_a_dropped_read_is_reported_once_reads_recover()
    {
        MasterAnswers("Op", 1001);
        await PollOnce();

        MasterStateReadFails();
        await PollOnce();

        MasterAnswers("SafeOp", 1001);
        await PollOnce();

        Emitted().Should().SatisfyRespectively(
            first => first.Should().BeOfType<MasterDiagnosticsDegradedEvent>()
                .Which.Degraded.Should().BeTrue(),
            second => second.Should().BeOfType<MasterDiagnosticsDegradedEvent>()
                .Which.Degraded.Should().BeFalse(),
            third => third.Should().BeOfType<MasterStateChangedEvent>()
                .Which.Should().BeEquivalentTo(new
                {
                    CurrentState = "SafeOp",
                    PreviousState = "Op",
                }));
    }

    [Fact]
    public async Task A_degraded_cycle_does_not_become_the_change_detection_baseline()
    {
        MasterAnswers("Op", 1001);
        await PollOnce();
        var knownGood = _cache.GetSnapshot(PlcId, MasterId);

        MasterStateReadFails();
        await PollOnce();

        _cache.GetSnapshot(PlcId, MasterId).Should().BeSameAs(knownGood,
            "a cycle that could not read the master must leave the last known-good reading in place");
        _cache.IsDegraded(PlcId, MasterId).Should().BeTrue();
    }

    [Fact]
    public async Task Recovery_clears_the_degraded_marker_and_emits_the_recovery_event()
    {
        MasterAnswers("Op", 1001);
        await PollOnce();

        MasterStateReadFails();
        await PollOnce();

        MasterAnswers("Op", 1001);
        await PollOnce();

        _cache.IsDegraded(PlcId, MasterId).Should().BeFalse();
        Emitted().OfType<MasterDiagnosticsDegradedEvent>().Should().SatisfyRespectively(
            entering => entering.Degraded.Should().BeTrue(),
            leaving =>
            {
                leaving.Degraded.Should().BeFalse();
                leaving.Reason.Should().BeNull();
            });
    }

    // A master unreachable from the very first cycle has no last known-good snapshot to fall back
    // on. It must still tell a subscriber, rather than being invisible until a read succeeds.
    [Fact]
    public async Task A_master_that_is_unreachable_from_the_first_cycle_still_reports_degraded()
    {
        MasterStateReadFails();
        SlaveListReadFails();

        await PollOnce();

        Emitted().Should().ContainSingle()
            .Which.Should().BeOfType<MasterDiagnosticsDegradedEvent>()
            .Which.Degraded.Should().BeTrue();
        _cache.GetSnapshot(PlcId, MasterId).Should().BeNull();
    }

    // The other half of the contract: a bus that genuinely empties out, and a master that
    // genuinely reports a state outside ETG.1000's set, are still reportable. Suppressing these
    // was the cost the old sentinel guards paid.
    [Fact]
    public async Task A_master_that_genuinely_reports_zero_slaves_still_emits_presence_events()
    {
        MasterAnswers("Op", 1001, 1002);
        await PollOnce();

        MasterAnswers("Op");
        await PollOnce();

        Emitted().OfType<MasterDiagnosticsDegradedEvent>().Should().BeEmpty();
        Emitted().OfType<SlavePresenceChangedEvent>().Should().HaveCount(2)
            .And.OnlyContain(e => !e.IsPresent);
    }

    [Fact]
    public async Task A_master_that_genuinely_reports_an_unknown_state_still_emits_a_state_change()
    {
        MasterAnswers("Op", 1001);
        await PollOnce();

        MasterAnswers("Unknown", 1001);
        await PollOnce();

        Emitted().OfType<MasterDiagnosticsDegradedEvent>().Should().BeEmpty();
        Emitted().Should().ContainSingle()
            .Which.Should().BeOfType<MasterStateChangedEvent>()
            .Which.CurrentState.Should().Be("Unknown");
    }

    // The CLR type name is what a consumer sees, and adsify's handler uses it verbatim as the
    // SignalR method name — so the wire names carry the "Event" suffix. Pinned because
    // docs/site/content/docs/api/ethercat.md documents these names to client authors, and listed
    // them without the suffix until #43 — a client registering the documented handler would never
    // have fired. The name-to-wire mapping itself now lives in adsify's SignalREtherCatHandler and
    // is pinned by SignalREtherCatHandlerTests; what this holds is that the monitor hands over
    // these six types, in this order.
    [Fact]
    public async Task Events_reach_subscribers_under_their_type_names()
    {
        MasterAnswers("Op", 1001);
        await PollOnce();

        MasterStateReadFails();
        await PollOnce();

        MasterAnswers("SafeOp", 1001);
        await PollOnce();

        EmittedNames().Should().Equal(
            nameof(MasterDiagnosticsDegradedEvent),
            nameof(MasterDiagnosticsDegradedEvent),
            nameof(MasterStateChangedEvent));
    }

    // Degradation is per (plcId, masterDeviceId), not per monitor. A rack with two masters
    // must not go quiet on the healthy one because its sibling stopped answering.
    //
    // This is the only test here that distinguishes per-master state from a single monitor-wide
    // flag: every other test uses one master, so a `bool _degraded` field on the monitor would
    // leave them all green.
    [Fact]
    public async Task Degradation_is_scoped_to_one_master_and_does_not_silence_its_siblings()
    {
        MasterAnswers(MasterNetId, "Op", 1001);
        MasterAnswers(SecondMasterNetId, "Op", 2001);
        await PollOnce();
        await PollSecondMasterOnce();

        // Master 0's reads drop. Master 1 keeps answering, and genuinely changes state.
        MasterStateReadFails(MasterNetId);
        MasterAnswers(SecondMasterNetId, "SafeOp", 2001);
        await PollOnce();
        await PollSecondMasterOnce();

        _cache.IsDegraded(PlcId, MasterId).Should().BeTrue();
        _cache.IsDegraded(PlcId, SecondMasterId).Should().BeFalse(
            "one master's dropped reads say nothing about another master on the same PLC");

        // Exactly one degraded event, for master 0 only — master 1 must not be reported degraded
        // and must not emit a spurious recovery event.
        Emitted().OfType<MasterDiagnosticsDegradedEvent>().Should().ContainSingle()
            .Which.MasterId.Should().Be(MasterId);

        // Master 1's genuine state change is still reported.
        Emitted().OfType<MasterStateChangedEvent>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                MasterId = SecondMasterId,
                CurrentState = "SafeOp",
                PreviousState = "Op",
            });

        // And master 1's own snapshot advanced while master 0's stayed at the last known-good.
        _cache.GetSnapshot(PlcId, SecondMasterId)!.MasterState.CurrentState.Should().Be("SafeOp");
        _cache.GetSnapshot(PlcId, MasterId)!.MasterState.CurrentState.Should().Be("Op");
    }

    // Degradation is tracked whether or not notifications are enabled, so the marker stays
    // coherent for the REST surface and for the next cycle's transition check.
    [Fact]
    public async Task Degradation_is_recorded_even_when_notifications_are_disabled()
    {
        _options.EnableNotifications = false;

        MasterAnswers("Op", 1001);
        await PollOnce();

        MasterStateReadFails();
        await PollOnce();

        _cache.IsDegraded(PlcId, MasterId).Should().BeTrue();
        Emitted().Should().BeEmpty();
    }

    // #42: a decorating read failing must not be silent, and must not degrade the master either.
    // The master's state and every slave's state were genuinely read, so the snapshot is fresh and
    // events still flow — what is missing is NAMED rather than filled in.
    [Fact]
    public async Task A_failed_frame_statistics_read_is_named_without_degrading_the_master()
    {
        MasterAnswers("Op", 1001);
        _client.GetFrameStatisticsAsync(MasterNetId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FrameStatistics?>(null));

        await PollOnce();

        var snapshot = _cache.GetSnapshot(PlcId, MasterId);
        snapshot.Should().NotBeNull("a decorating-read failure still produces a usable reading");
        snapshot!.FrameStatistics.Should().BeNull("zeros would be a fabricated counter reading");
        snapshot.IncompleteReads.Should().HaveFlag(EtherCatReads.FrameStatistics);
        _cache.IsDegraded(PlcId, MasterId).Should().BeFalse("adsify was not blind, only partial");
    }

    // The complement, and the invariant that separates a partial cycle from a blind one.
    [Fact]
    public async Task State_changes_are_still_reported_through_a_partial_cycle()
    {
        MasterAnswers("Op", 1001);
        await PollOnce();

        MasterAnswers("SafeOp", 1001);
        _client.GetFrameStatisticsAsync(MasterNetId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FrameStatistics?>(null));
        await PollOnce();

        Emitted().OfType<MasterStateChangedEvent>().Should().ContainSingle()
            .Which.CurrentState.Should().Be("SafeOp");
        Emitted().OfType<MasterDiagnosticsDegradedEvent>().Should().BeEmpty();
    }

    // A cycle where everything answered must report nothing missing, or the flag means nothing.
    [Fact]
    public async Task A_healthy_cycle_reports_no_incomplete_reads()
    {
        MasterAnswers("Op", 1001);

        await PollOnce();

        _cache.GetSnapshot(PlcId, MasterId)!.IncompleteReads.Should().Be(EtherCatReads.None);
    }

    // The whole scanned-identity list failing to answer at all: every slave's Scanned is null and
    // the flag is set. That much the flag still says reliably — but it is no longer the ONLY thing
    // that sets it (see A_per_slave_scanned_identity_failure_is_distinguishable_from_a_slave_absent_from_the_scan
    // below): the flag makes a null explainable, it does not confirm a failed read for any ONE
    // slave, and a slave genuinely off the bus can look identical to this in a cycle where it was
    // actually some OTHER slave's identity read that failed.
    [Fact]
    public async Task A_failed_scanned_identity_read_is_named()
    {
        MasterAnswers("Op", 1001);
        _client.GetScannedSlavesAsync(MasterNetId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EtherCatScannedSlave>?>(null));

        await PollOnce();

        _cache.GetSnapshot(PlcId, MasterId)!.IncompleteReads
            .Should().HaveFlag(EtherCatReads.ScannedIdentities);
    }

    [Fact]
    public async Task A_slave_absent_from_a_successful_scan_is_not_reported_as_a_failed_read()
    {
        MasterAnswers("Op", 1001);
        // The scan answered — it just does not list slave 1001, which means that slave is not
        // physically on the bus. That is a reading, not a failure.
        _client.GetScannedSlavesAsync(MasterNetId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EtherCatScannedSlave>?>([]));

        await PollOnce();

        _cache.GetSnapshot(PlcId, MasterId)!.IncompleteReads
            .Should().NotHaveFlag(EtherCatReads.ScannedIdentities);
    }

    // #42/Task 7: GetScannedSlavesAsync keeps a slave listed at its real address when only that
    // slave's own identity read (IG 0x11) failed, rather than zero-filling it or dropping it. A
    // dropped entry would be indistinguishable from "not on the bus" (the test above); a
    // zero-filled one would be the exact fabrication #42 exists to remove. Both must be visible
    // here: the slave stays present with its identity absent, and the cycle is named as partial.
    [Fact]
    public async Task A_per_slave_scanned_identity_failure_is_named_without_degrading_the_master()
    {
        MasterAnswers("Op", 1001);
        _client.GetScannedSlavesAsync(MasterNetId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EtherCatScannedSlave>?>(
            [
                new EtherCatScannedSlave
                {
                    PhysicalAddress = 1001,
                    VendorId = null,
                    ProductCode = null,
                    RevisionNumber = null,
                    SerialNumber = null,
                },
            ]));

        await PollOnce();

        var snapshot = _cache.GetSnapshot(PlcId, MasterId);
        snapshot.Should().NotBeNull("a per-slave decorating-read failure still produces a usable reading");
        var slave = snapshot!.Slaves.Single(s => s.PhysicalAddress == 1001);
        slave.Scanned.Should().NotBeNull("the slave WAS in the scan — only its identity failed");
        slave.Scanned!.VendorId.Should().BeNull("a fabricated zero would read as a real device");
        snapshot.IncompleteReads.Should().HaveFlag(EtherCatReads.ScannedIdentities);
        _cache.IsDegraded(PlcId, MasterId).Should().BeFalse("adsify was not blind, only partial");
    }

    // The three-way disambiguation in one cycle: a slave the scan reported but couldn't fully
    // read, side by side with a slave the scan never reported at all. The snapshot-wide flag is
    // set either way here (it cannot tell 1001 and 1002 apart on its own — see
    // EtherCatReads.ScannedIdentities's doc); what actually distinguishes them is each slave's OWN
    // Scanned nullness: 1001's stays non-null (identity absent inside it), 1002's is null outright.
    [Fact]
    public async Task A_per_slave_scanned_identity_failure_is_distinguishable_from_a_slave_absent_from_the_scan()
    {
        MasterAnswers("Op", 1001, 1002);
        _client.GetScannedSlavesAsync(MasterNetId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EtherCatScannedSlave>?>(
            [
                new EtherCatScannedSlave
                {
                    PhysicalAddress = 1001,
                    VendorId = null,
                    ProductCode = null,
                    RevisionNumber = null,
                    SerialNumber = null,
                },
                // 1002 is deliberately not in this list at all — genuinely absent from the scan,
                // not a failed read.
            ]));

        await PollOnce();

        var snapshot = _cache.GetSnapshot(PlcId, MasterId)!;
        snapshot.Slaves.Single(s => s.PhysicalAddress == 1001).Scanned.Should().NotBeNull(
            "1001 was scanned; only its identity read failed");
        snapshot.Slaves.Single(s => s.PhysicalAddress == 1002).Scanned.Should().BeNull(
            "1002 was never in the scan's answer at all");
        snapshot.IncompleteReads.Should().HaveFlag(EtherCatReads.ScannedIdentities);
    }

    // Fix round 1 (review finding 3): the "all four identity fields null together" invariant on
    // EtherCatScannedSlave is documented, not enforced by the type — it is publicly constructible,
    // and only EtherCatClient's own construction keeps the four fields in lockstep today. A
    // producer that set some but not all must still trip the flag: EtherCatService's DTO mapping
    // already requires all four non-null before it will build a SlaveIdentityDto, so a
    // partially-populated entry reaches the wire as scannedIdentity: null regardless — the flag
    // must agree, or that null arrives unexplained.
    [Fact]
    public async Task A_partially_populated_scanned_identity_still_sets_the_flag()
    {
        MasterAnswers("Op", 1001);
        _client.GetScannedSlavesAsync(MasterNetId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EtherCatScannedSlave>?>(
            [
                new EtherCatScannedSlave
                {
                    PhysicalAddress = 1001,
                    VendorId = 2, // set...
                    ProductCode = null, // ...but not the rest: an invariant violation the type
                    RevisionNumber = null, // does not prevent, and the flag must still catch.
                    SerialNumber = null,
                },
            ]));

        await PollOnce();

        _cache.GetSnapshot(PlcId, MasterId)!.IncompleteReads.Should().HaveFlag(
            EtherCatReads.ScannedIdentities,
            "a partially-identified entry is still an unanswered identity read, not a clean one");
    }

    // The follow-on bug the docs currently warn about: rates are a delta against the previous
    // counters, so a dropped statistics read made the NEXT cycle's rates spike against zeros.
    // With the counters absent rather than zeroed, the rate is unknown — not a large number.
    [Fact]
    public async Task Frame_rates_are_absent_rather_than_spiked_after_a_dropped_counter_read()
    {
        MasterAnswers("Op", 1001);
        _client.GetFrameStatisticsAsync(MasterNetId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FrameStatistics?>(null));
        await PollOnce();

        _client.GetFrameStatisticsAsync(MasterNetId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FrameStatistics?>(
                new FrameStatistics { CyclicSendFrames = 5_000_000 }));
        await PollOnce();

        var stats = _cache.GetSnapshot(PlcId, MasterId)!.FrameStatistics;
        stats.Should().NotBeNull();
        stats!.CyclicSendFrames.Should().Be(5_000_000);
        stats.CyclicFramesPerSecond.Should().BeNull("there is no previous counter to delta against");
    }

    // The sharpest instance of #42. EtherCatClient hardcodes IdentityMatch = true, and the polling
    // service used to fall back to false when the detail read failed — so a dropped read rendered as
    // "this slave is not the device the project configured", a fabricated fault on a healthy rack.
    [Fact]
    public async Task A_failed_detail_read_leaves_identity_absent_rather_than_mismatched()
    {
        MasterAnswers("Op", 1001);
        _client.GetSlaveDetailAsync(MasterNetId, (ushort)1001, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<EtherCatSlaveDetail?>(null));

        await PollOnce();

        var snapshot = _cache.GetSnapshot(PlcId, MasterId);
        snapshot!.Slaves.Single().Detail.Should().BeNull();
        snapshot.IncompleteReads.Should().HaveFlag(EtherCatReads.SlaveDetail);
        _cache.IsDegraded(PlcId, MasterId).Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_counter_read_leaves_counters_absent_rather_than_zero()
    {
        MasterAnswers("Op", 1001);
        _client.GetSlaveErrorCountersAsync(MasterNetId, (ushort)1001, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SlaveErrorCounters?>(null));

        await PollOnce();

        var snapshot = _cache.GetSnapshot(PlcId, MasterId);
        snapshot!.Slaves.Single().ErrorCounters.Should().BeNull("zeroed counters read as a healthy port");
        snapshot.IncompleteReads.Should().HaveFlag(EtherCatReads.SlaveErrorCounters);
    }

    // A dropped counter read must not re-arm an alarm that has already fired: the CRC threshold event
    // is one-shot per port, and re-firing it on recovery would alarm an operator for a bus condition
    // that never changed.
    [Fact]
    public async Task A_failed_counter_read_neither_emits_nor_re_arms_the_crc_alarm()
    {
        _client.GetSlaveErrorCountersAsync(MasterNetId, (ushort)1001, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SlaveErrorCounters?>(new SlaveErrorCounters
            {
                PhysicalAddress = 1001,
                AbnormalStateChanges = 0,
                Ports = [new PortErrorCounters
                    { Port = "A", CrcErrors = 500, ForwardedCrcErrors = 0, LostLinkCount = 0 }],
            }));
        MasterAnswers("Op", 1001);
        await PollOnce();  // baseline
        await PollOnce();  // fires the threshold event once

        var afterAlarm = Emitted().OfType<CrcErrorThresholdExceededEvent>().Count();
        afterAlarm.Should().Be(1);

        // Counters go dark, then come back still above the threshold.
        _client.GetSlaveErrorCountersAsync(MasterNetId, (ushort)1001, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SlaveErrorCounters?>(null));
        await PollOnce();

        _client.GetSlaveErrorCountersAsync(MasterNetId, (ushort)1001, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SlaveErrorCounters?>(new SlaveErrorCounters
            {
                PhysicalAddress = 1001,
                AbnormalStateChanges = 0,
                Ports = [new PortErrorCounters
                    { Port = "A", CrcErrors = 500, ForwardedCrcErrors = 0, LostLinkCount = 0 }],
            }));
        await PollOnce();

        Emitted().OfType<CrcErrorThresholdExceededEvent>().Count().Should().Be(1,
            "the alarm already fired and the bus condition never changed");
    }

    // -- the poll cycle budget ----------------------------------------------------

    // #43: one poll cycle stretched from 1 s to ~2 minutes because every failed read burned its full
    // 10 s timeout and the reads run sequentially. The budget caps that.
    [Fact]
    public async Task A_cycle_that_overruns_its_budget_degrades_the_master()
    {
        MasterAnswers("Op", 1001, 1002);
        _client.GetSlaveDetailAsync(MasterNetId, Arg.Any<ushort>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _clock.Advance(TimeSpan.FromSeconds(6));   // budget is 5 s
                return Task.FromResult<EtherCatSlaveDetail?>(SlaveDetail());
            });

        await PollOnce();

        _cache.IsDegraded(PlcId, MasterId).Should().BeTrue();
        Emitted().OfType<MasterDiagnosticsDegradedEvent>().Should().ContainSingle()
            .Which.Reason.Should().Be(EtherCatMonitor.CycleBudgetExceeded);
    }

    // The fabricated-disappearance guard. A cycle abandoned halfway through the slave loop has read
    // some slaves and not others; storing that list would compare against the previous snapshot as
    // "the rest of the rack disappeared".
    [Fact]
    public async Task An_overrunning_cycle_never_stores_a_truncated_slave_list()
    {
        MasterAnswers("Op", 1001, 1002, 1003);
        await PollOnce();   // a complete, known-good baseline

        var baseline = _cache.GetSnapshot(PlcId, MasterId);
        baseline!.Slaves.Should().HaveCount(3);

        _client.GetSlaveDetailAsync(MasterNetId, Arg.Any<ushort>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _clock.Advance(TimeSpan.FromSeconds(6));
                return Task.FromResult<EtherCatSlaveDetail?>(SlaveDetail());
            });
        await PollOnce();

        _cache.GetSnapshot(PlcId, MasterId).Should().BeSameAs(baseline,
            "an abandoned cycle keeps the last known-good reading");
        Emitted().OfType<SlavePresenceChangedEvent>().Should().BeEmpty(
            "no slave left the bus — adsify simply ran out of time");
    }

    // Three slaves, not one, so the per-slave budget check runs twice with time already on the
    // clock: a check that fired on any elapsed time at all rather than on the limit would pass with
    // a single slave, because nothing consults the budget after that slave's read.
    [Fact]
    public async Task A_cycle_inside_its_budget_is_unaffected()
    {
        MasterAnswers("Op", 1001, 1002, 1003);
        _client.GetSlaveDetailAsync(MasterNetId, Arg.Any<ushort>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _clock.Advance(TimeSpan.FromMilliseconds(50));
                return Task.FromResult<EtherCatSlaveDetail?>(SlaveDetail());
            });

        await PollOnce();

        _cache.IsDegraded(PlcId, MasterId).Should().BeFalse();
        _cache.GetSnapshot(PlcId, MasterId).Should().NotBeNull()
            .And.Subject.As<EtherCatSnapshot>().Slaves.Should().HaveCount(3,
                "every slave inside the budget is read, not just the ones before the first check");
    }

    // The check between the gating reads and the rest, which the per-slave check cannot stand in
    // for: a master reporting zero configured slaves never enters the slave loop, so without this
    // one a cycle that spent its whole budget on the two gating reads would still store a snapshot
    // and clear the degraded marker.
    [Fact]
    public async Task A_cycle_that_spends_its_budget_on_the_gating_reads_is_abandoned()
    {
        MasterAnswers("Op");   // zero configured slaves
        _client.GetConfiguredSlavesAsync(MasterNetId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _clock.Advance(TimeSpan.FromSeconds(6));
                return Task.FromResult<IReadOnlyList<EtherCatSlaveInfo>?>([]);
            });

        await PollOnce();

        _cache.GetSnapshot(PlcId, MasterId).Should().BeNull(
            "an over-budget cycle has no complete reading to store");
        _cache.IsDegraded(PlcId, MasterId).Should().BeTrue();
        Emitted().OfType<MasterDiagnosticsDegradedEvent>().Should().ContainSingle()
            .Which.Reason.Should().Be(EtherCatMonitor.CycleBudgetExceeded);
    }

    // The budget must reach the client calls, not just be checked between them — otherwise a single
    // read still burns 10 s × 2 attempts before anyone notices.
    [Fact]
    public async Task The_budget_cancels_a_read_that_is_already_in_flight()
    {
        MasterAnswers("Op", 1001);
        CancellationToken observed = default;
        _client.GetSlaveDetailAsync(MasterNetId, Arg.Any<ushort>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                observed = ci.ArgAt<CancellationToken>(2);
                _clock.Advance(TimeSpan.FromSeconds(6));
                observed.ThrowIfCancellationRequested();
                return Task.FromResult<EtherCatSlaveDetail?>(SlaveDetail());
            });

        await PollOnce();

        observed.IsCancellationRequested.Should().BeTrue(
            "the cycle token must reach the client, not just bound the loop");
        _cache.IsDegraded(PlcId, MasterId).Should().BeTrue();
    }

    // The same proof one level up: the GATING reads must run under the cycle token too, or a master
    // state read that hangs burns its full 10 s × 2 attempts before the budget is ever consulted.
    [Fact]
    public async Task The_cycle_token_reaches_the_gating_reads_too()
    {
        MasterAnswers("Op", 1001);
        CancellationToken observedByMasterState = default;
        CancellationToken observedBySlaveList = default;
        _client.GetMasterStateAsync(MasterNetId, Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                observedByMasterState = ci.ArgAt<CancellationToken>(1);
                return Task.FromResult<EtherCatMasterState?>(new EtherCatMasterState
                {
                    CurrentState = "Op",
                    RequestedState = "Op",
                    SlaveCount = 1,
                });
            });
        _client.GetConfiguredSlavesAsync(MasterNetId, Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                observedBySlaveList = ci.ArgAt<CancellationToken>(1);
                _clock.Advance(TimeSpan.FromSeconds(6));
                return Task.FromResult<IReadOnlyList<EtherCatSlaveInfo>?>([SlaveInfo(1001)]);
            });

        await PollOnce();

        observedByMasterState.CanBeCanceled.Should().BeTrue(
            "the master state read must run under the cycle token, not CancellationToken.None");
        observedByMasterState.IsCancellationRequested.Should().BeTrue(
            "the budget that expired during the slave-list read cancels the cycle token both reads share");
        observedBySlaveList.IsCancellationRequested.Should().BeTrue();
    }

    // Shutdown must stay distinguishable from an overrun: the monitor is stopping, not degraded.
    // The guard is `!ct.IsCancellationRequested && budgetCts.IsCancellationRequested` — without the
    // first half, every shutdown mid-cycle would emit a spurious "budget exceeded" degradation.
    [Fact]
    public async Task Shutdown_cancellation_propagates_rather_than_degrading()
    {
        MasterAnswers("Op", 1001);
        using var stopping = new CancellationTokenSource();
        _client.GetSlaveDetailAsync(MasterNetId, Arg.Any<ushort>(), Arg.Any<CancellationToken>())
            .Returns<Task<EtherCatSlaveDetail?>>(_ =>
            {
                // The clock never advances, so the budget is NOT the reason this cycle ends.
                stopping.Cancel();
                throw new OperationCanceledException(stopping.Token);
            });

        var act = () => _sut.PollMasterAsync(PlcId, _master, _options, stopping.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _cache.IsDegraded(PlcId, MasterId).Should().BeFalse();
        Emitted().OfType<MasterDiagnosticsDegradedEvent>().Should().BeEmpty();
    }

    // The half of the guard the test above cannot reach. When the host stops DURING a cycle that has
    // also gone over budget, both conditions hold at once — and the monitor is shutting down, not
    // degraded. Without `!ct.IsCancellationRequested` the handler would swallow the shutdown and
    // emit a spurious "budget exceeded" degradation on the way out.
    [Fact]
    public async Task Shutdown_during_an_over_budget_cycle_still_propagates_rather_than_degrading()
    {
        MasterAnswers("Op", 1001);
        using var stopping = new CancellationTokenSource();
        _client.GetSlaveDetailAsync(MasterNetId, Arg.Any<ushort>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _clock.Advance(TimeSpan.FromSeconds(6));   // the budget expires ...
                stopping.Cancel();                          // ... and the host stops, together
                ci.ArgAt<CancellationToken>(2).ThrowIfCancellationRequested();
                return Task.FromResult<EtherCatSlaveDetail?>(SlaveDetail());
            });

        var act = () => _sut.PollMasterAsync(PlcId, _master, _options, stopping.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _cache.IsDegraded(PlcId, MasterId).Should().BeFalse();
        Emitted().OfType<MasterDiagnosticsDegradedEvent>().Should().BeEmpty();
    }

    // And the other half. A cancellation that is neither the caller's nor the budget's must not be
    // relabelled "poll cycle budget exceeded" — that reason names a specific, tunable cause, and
    // attaching it to an unexplained cancellation sends an operator to the wrong knob.
    [Fact]
    public async Task An_unrelated_cancellation_is_not_reported_as_a_budget_overrun()
    {
        MasterAnswers("Op", 1001);
        using var unrelated = new CancellationTokenSource();
        unrelated.Cancel();
        _client.GetSlaveDetailAsync(MasterNetId, Arg.Any<ushort>(), Arg.Any<CancellationToken>())
            .Returns<Task<EtherCatSlaveDetail?>>(_ =>
                // The clock never advances: the budget is intact and the caller never asked to stop.
                throw new OperationCanceledException(unrelated.Token));

        var act = () => PollOnce();

        await act.Should().ThrowAsync<OperationCanceledException>();
        _cache.IsDegraded(PlcId, MasterId).Should().BeFalse();
        Emitted().OfType<MasterDiagnosticsDegradedEvent>().Should().BeEmpty();
    }

    // An overrun is a transition like any other degradation: a rack that stays too slow to poll must
    // not emit one event per second. This is what pins EnterDegradedAsync being handed wasDegraded
    // read at the START of the cycle rather than after the reads that consumed the budget.
    [Fact]
    public async Task Repeated_overrunning_cycles_emit_one_event_not_one_per_cycle()
    {
        MasterAnswers("Op", 1001, 1002);
        _client.GetSlaveDetailAsync(MasterNetId, Arg.Any<ushort>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _clock.Advance(TimeSpan.FromSeconds(6));
                return Task.FromResult<EtherCatSlaveDetail?>(SlaveDetail());
            });

        for (int cycle = 0; cycle < 5; cycle++)
            await PollOnce();

        Emitted().OfType<MasterDiagnosticsDegradedEvent>().Should().ContainSingle()
            .Which.Reason.Should().Be(EtherCatMonitor.CycleBudgetExceeded);
    }

    // The same transition rule for the OTHER route into an overrun: a read cancelled in flight,
    // which lands in the wrapper's catch rather than in a between-reads check. The wrapper reads
    // wasDegraded at the START of the cycle for exactly this reason.
    [Fact]
    public async Task Repeated_cancelled_in_flight_reads_emit_one_event_not_one_per_cycle()
    {
        MasterAnswers("Op", 1001);
        _client.GetSlaveDetailAsync(MasterNetId, Arg.Any<ushort>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _clock.Advance(TimeSpan.FromSeconds(6));
                ci.ArgAt<CancellationToken>(2).ThrowIfCancellationRequested();
                return Task.FromResult<EtherCatSlaveDetail?>(SlaveDetail());
            });

        for (int cycle = 0; cycle < 5; cycle++)
            await PollOnce();

        Emitted().OfType<MasterDiagnosticsDegradedEvent>().Should().ContainSingle()
            .Which.Reason.Should().Be(EtherCatMonitor.CycleBudgetExceeded);
    }

    // Both overrun routes report the same operator-visible condition, so they must cost the same to
    // watch. The advice Warning on the cancelled-read route sits outside EnterDegradedAsync's
    // transition gate, so ungated it fires once per poll interval — one a second on the shipped
    // PollingIntervalMs — where the between-reads route fires once, ever.
    [Fact]
    public async Task Repeated_overruns_log_the_budget_advice_once_not_once_per_cycle()
    {
        MasterAnswers("Op", 1001);
        _client.GetSlaveDetailAsync(MasterNetId, Arg.Any<ushort>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _clock.Advance(TimeSpan.FromSeconds(6));
                ci.ArgAt<CancellationToken>(2).ThrowIfCancellationRequested();
                return Task.FromResult<EtherCatSlaveDetail?>(SlaveDetail());
            });

        for (int cycle = 0; cycle < 5; cycle++)
            await PollOnce();

        Warnings().Where(warning => warning.Contains("PollCycleBudgetMs")).Should().ContainSingle();
    }

    // #43's own knob must not be able to reinstate #43. CancellationTokenSource rejects any delay
    // below -1 ms, and that ArgumentOutOfRangeException would escape to PollPlcAsync's generic
    // handler — which logs it and steps to the next master, leaving this one un-degraded with a
    // frozen snapshot still reporting diagnosticsDegraded: false. Silent staleness, reached through
    // a typo in the very option that removes it.
    [Theory]
    [InlineData(-5000)]  // below -1 ms: CancellationTokenSource throws without the guard
    [InlineData(-1)]     // Timeout.InfiniteTimeSpan: a deadline that never fires
    [InlineData(0)]      // a deadline that has already passed
    public async Task An_unusable_budget_degrades_the_master_rather_than_throwing(int budgetMs)
    {
        MasterAnswers("Op", 1001);
        await PollOnce();
        var baseline = _cache.GetSnapshot(PlcId, MasterId);
        baseline.Should().NotBeNull();

        _options.PollCycleBudgetMs = budgetMs;
        var act = () => PollOnce();

        await act.Should().NotThrowAsync(
            "a misconfigured budget must not escape to PollPlcAsync's generic handler");
        _cache.IsDegraded(PlcId, MasterId).Should().BeTrue(
            "a master whose cycle adsify cannot bound must not sit at diagnosticsDegraded: false");
        _cache.GetSnapshot(PlcId, MasterId).Should().BeSameAs(baseline,
            "an unusable budget produces no reading, so the last known-good one stands");
        Emitted().OfType<MasterDiagnosticsDegradedEvent>().Should().ContainSingle()
            .Which.Reason.Should().Be(EtherCatMonitor.CycleBudgetExceeded);
    }

    // A degraded master with no explanation is just a different silence: the Warning has to name
    // the option and the offending value, or an operator cannot tell a misconfiguration from a bus
    // that is genuinely too slow.
    [Fact]
    public async Task An_unusable_budget_names_the_option_and_its_value_in_a_warning()
    {
        _options.PollCycleBudgetMs = -5000;
        MasterAnswers("Op", 1001);

        await PollOnce();

        Warnings().Should().ContainSingle(warning =>
            warning.Contains("PollCycleBudgetMs") && warning.Contains("-5000"));
    }

    // And it, too, is a standing condition rather than an event.
    [Fact]
    public async Task An_unusable_budget_logs_and_emits_once_not_once_per_cycle()
    {
        _options.PollCycleBudgetMs = -5000;
        MasterAnswers("Op", 1001);

        for (int cycle = 0; cycle < 5; cycle++)
            await PollOnce();

        Warnings().Where(warning => warning.Contains("PollCycleBudgetMs")).Should().ContainSingle();
        Emitted().OfType<MasterDiagnosticsDegradedEvent>().Should().ContainSingle();
    }

    // The overrun degradation must reach subscribers. EnterDegradedAsync broadcasts over SignalR,
    // so it has to be handed the OUTER token — handing it the cycle token that just fired would
    // kill the notification with the very budget that triggered it, leaving the cache degraded and
    // the subscriber none the wiser.
    [Fact]
    public async Task An_overrun_notification_survives_the_token_that_triggered_it()
    {
        MasterAnswers("Op", 1001);
        _client.GetSlaveDetailAsync(MasterNetId, Arg.Any<ushort>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _clock.Advance(TimeSpan.FromSeconds(6));
                ci.ArgAt<CancellationToken>(2).ThrowIfCancellationRequested();
                return Task.FromResult<EtherCatSlaveDetail?>(SlaveDetail());
            });

        await PollOnce();

        var sendTokens = HandleCalls()
            .Select(call => (CancellationToken)call.GetArguments()[2]!)
            .ToList();

        sendTokens.Should().ContainSingle();
        sendTokens[0].IsCancellationRequested.Should().BeFalse(
            "the broadcast must outlive the budget that triggered it");
    }
}
