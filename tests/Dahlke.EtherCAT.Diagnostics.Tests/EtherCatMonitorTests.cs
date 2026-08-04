using Dahlke.EtherCAT.Diagnostics;
using Dahlke.TwinCAT.Ads;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Dahlke.EtherCAT.Diagnostics.Tests;

public class EtherCatMonitorTests
{
    private const int DefaultCrcThreshold = 100;
    private const int MasterId = 1;

    private static EtherCatSnapshot CreateSnapshot(
        string currentState = "OP",
        string requestedState = "OP",
        List<EtherCatSlaveSnapshot>? slaves = null,
        List<SyncUnitInfo>? syncUnits = null) => new()
    {
        PlcId = "plc1",
        MasterAmsNetId = "5.80.192.39.4.1",
        MasterDeviceId = MasterId,
        MasterName = "EtherCAT Master",
        Timestamp = DateTimeOffset.UtcNow,
        MasterState = new EtherCatMasterState
        {
            CurrentState = currentState,
            RequestedState = requestedState,
            SlaveCount = slaves?.Count ?? 0,
        },
        FrameStatistics = new FrameStatistics(),
        IncompleteReads = EtherCatReads.None,
        Slaves = slaves ?? [],
        SyncUnits = syncUnits ?? [],
    };

    private static EtherCatSlaveSnapshot CreateSlave(
        ushort address = 1001,
        string name = "EL1008",
        string currentState = "OP",
        bool isPresent = true,
        bool hasError = false,
        List<PortErrorCounters>? portErrors = null) => new()
    {
        PhysicalAddress = address,
        AutoIncrementAddress = 0,
        Name = name,
        Type = "Digital Input",
        CurrentState = currentState,
        RequestedState = "OP",
        IsPresent = isPresent,
        HasError = hasError,
        IsDisabled = false,
        Detail = new EtherCatSlaveDetail
        {
            IdentityMatch = true,
            InitError = false,
            ConfiguredVendorId = 2,
            ConfiguredProductCode = 0x03F83052,
            ConfiguredRevisionNumber = 0x00120000,
            ConfiguredSerialNumber = 0,
            ScannedVendorId = 2,
            ScannedProductCode = 0x03F83052,
            ScannedRevisionNumber = 0x00120000,
            ScannedSerialNumber = 0,
            Ports = [new SlavePortInfo { Port = "A", Physic = "EBus", Configured = true, LinkState = true }],
        },
        ErrorCounters = new SlaveErrorCounters
        {
            PhysicalAddress = address,
            AbnormalStateChanges = 0,
            Ports = portErrors ?? [],
        },
        Scanned = null,
    };

    [Fact]
    public void DetectChanges_returns_empty_when_no_previous_snapshot()
    {
        var current = CreateSnapshot();
        var crcNotified = new HashSet<(int, ushort, string)>();

        var events = EtherCatMonitor.DetectChanges(null, current, DefaultCrcThreshold, crcNotified);

        events.Should().BeEmpty();
    }

    [Fact]
    public void DetectChanges_emits_MasterStateChanged_when_currentState_differs()
    {
        var previous = CreateSnapshot(currentState: "SAFEOP");
        var current = CreateSnapshot(currentState: "OP");
        var crcNotified = new HashSet<(int, ushort, string)>();

        var events = EtherCatMonitor.DetectChanges(previous, current, DefaultCrcThreshold, crcNotified);

        events.Should().ContainSingle()
            .Which.Should().BeOfType<MasterStateChangedEvent>()
            .Which.Should().BeEquivalentTo(new
            {
                MasterId,
                CurrentState = "OP",
                PreviousState = "SAFEOP",
                RequestedState = "OP",
            });
    }

    [Fact]
    public void DetectChanges_emits_MasterStateChanged_when_requestedState_differs()
    {
        var previous = CreateSnapshot(requestedState: "OP");
        var current = CreateSnapshot(requestedState: "INIT");
        var crcNotified = new HashSet<(int, ushort, string)>();

        var events = EtherCatMonitor.DetectChanges(previous, current, DefaultCrcThreshold, crcNotified);

        events.Should().ContainSingle()
            .Which.Should().BeOfType<MasterStateChangedEvent>()
            .Which.Should().BeEquivalentTo(new
            {
                MasterId,
                CurrentState = "OP",
                PreviousState = "OP",
                RequestedState = "INIT",
            });
    }

    [Fact]
    public void DetectChanges_does_not_emit_when_states_equal()
    {
        var previous = CreateSnapshot();
        var current = CreateSnapshot();
        var crcNotified = new HashSet<(int, ushort, string)>();

        var events = EtherCatMonitor.DetectChanges(previous, current, DefaultCrcThreshold, crcNotified);

        events.Should().BeEmpty();
    }

    [Fact]
    public void DetectChanges_emits_SlaveStateChanged_when_slave_currentState_differs()
    {
        var previous = CreateSnapshot(slaves: [CreateSlave(currentState: "SAFEOP")]);
        var current = CreateSnapshot(slaves: [CreateSlave(currentState: "OP")]);
        var crcNotified = new HashSet<(int, ushort, string)>();

        var events = EtherCatMonitor.DetectChanges(previous, current, DefaultCrcThreshold, crcNotified);

        events.Should().ContainSingle()
            .Which.Should().BeOfType<SlaveStateChangedEvent>()
            .Which.Should().BeEquivalentTo(new
            {
                MasterId,
                Address = (ushort)1001,
                CurrentState = "OP",
                PreviousState = "SAFEOP",
                HasError = false,
            });
    }

    [Fact]
    public void DetectChanges_emits_SlaveStateChanged_when_hasError_changes()
    {
        var previous = CreateSnapshot(slaves: [CreateSlave(hasError: false)]);
        var current = CreateSnapshot(slaves: [CreateSlave(hasError: true)]);
        var crcNotified = new HashSet<(int, ushort, string)>();

        var events = EtherCatMonitor.DetectChanges(previous, current, DefaultCrcThreshold, crcNotified);

        events.Should().ContainSingle()
            .Which.Should().BeOfType<SlaveStateChangedEvent>()
            .Which.Should().BeEquivalentTo(new
            {
                MasterId,
                Address = (ushort)1001,
                HasError = true,
            });
    }

    [Fact]
    public void DetectChanges_emits_SlavePresenceChanged_when_isPresent_changes()
    {
        var previous = CreateSnapshot(slaves: [CreateSlave(isPresent: true)]);
        var current = CreateSnapshot(slaves: [CreateSlave(isPresent: false)]);
        var crcNotified = new HashSet<(int, ushort, string)>();

        var events = EtherCatMonitor.DetectChanges(previous, current, DefaultCrcThreshold, crcNotified);

        var presenceEvent = events.OfType<SlavePresenceChangedEvent>().Should().ContainSingle().Which;
        presenceEvent.Should().BeEquivalentTo(new
        {
            MasterId,
            Address = (ushort)1001,
            IsPresent = false,
        });
    }

    // Replaces DetectChanges_suppresses_disappearance_events_when_the_slave_list_goes_fully_empty,
    // which pinned the old sentinel guard. That guard existed because a failed slave-count read
    // and a genuinely empty bus both arrived here as []. They no longer do:
    // IEtherCatClient.GetConfiguredSlavesAsync answers null for the failed read, and
    // PollMasterAsync never builds a snapshot from it — so an empty list reaching DetectChanges is
    // a master reporting zero configured slaves, and must be reported as such.
    [Fact]
    public void DetectChanges_emits_disappearance_events_when_the_master_reports_zero_slaves()
    {
        var previous = CreateSnapshot(slaves: [CreateSlave(address: 1001, name: "EL1008")]);
        var current = CreateSnapshot(slaves: []);
        var crcNotified = new HashSet<(int, ushort, string)>();

        var events = EtherCatMonitor.DetectChanges(previous, current, DefaultCrcThreshold, crcNotified);

        events.Should().ContainSingle()
            .Which.Should().BeOfType<SlavePresenceChangedEvent>()
            .Which.Should().BeEquivalentTo(new
            {
                MasterId,
                Address = (ushort)1001,
                Name = "EL1008",
                IsPresent = false,
            });
    }

    // A partial drop was always reported; it stays reported.
    [Fact]
    public void DetectChanges_still_emits_SlavePresenceChanged_when_some_but_not_all_slaves_disappear()
    {
        var previous = CreateSnapshot(slaves:
        [
            CreateSlave(address: 1001, name: "EL1008"),
            CreateSlave(address: 1002, name: "EL2004"),
        ]);
        var current = CreateSnapshot(slaves: [CreateSlave(address: 1001, name: "EL1008")]);
        var crcNotified = new HashSet<(int, ushort, string)>();

        var events = EtherCatMonitor.DetectChanges(previous, current, DefaultCrcThreshold, crcNotified);

        events.Should().ContainSingle()
            .Which.Should().BeOfType<SlavePresenceChangedEvent>()
            .Which.Should().BeEquivalentTo(new
            {
                MasterId,
                Address = (ushort)1002,
                Name = "EL2004",
                IsPresent = false,
            });
    }

    // Replaces the two DetectChanges_suppresses_MasterStateChanged_..._Unknown tests, which pinned
    // the old sentinel guard. "Unknown" is no longer overloaded: GetMasterStateAsync answers null
    // for a failed read and a value for anything the master said, so "Unknown" reaching here means
    // the master reported a raw state byte outside ETG.1000's 1/2/3/4/8 — a genuine reading, and
    // reportable in both directions.
    [Fact]
    public void DetectChanges_emits_MasterStateChanged_when_the_master_reports_Unknown()
    {
        var previous = CreateSnapshot(currentState: "Op", requestedState: "Op");
        var current = CreateSnapshot(currentState: "Unknown", requestedState: "Unknown");
        var crcNotified = new HashSet<(int, ushort, string)>();

        var events = EtherCatMonitor.DetectChanges(previous, current, DefaultCrcThreshold, crcNotified);

        events.Should().ContainSingle()
            .Which.Should().BeOfType<MasterStateChangedEvent>()
            .Which.Should().BeEquivalentTo(new
            {
                MasterId,
                CurrentState = "Unknown",
                PreviousState = "Op",
            });
    }

    [Fact]
    public void DetectChanges_emits_MasterStateChanged_when_the_master_leaves_Unknown()
    {
        var previous = CreateSnapshot(currentState: "Unknown", requestedState: "Unknown");
        var current = CreateSnapshot(currentState: "Op", requestedState: "Op");
        var crcNotified = new HashSet<(int, ushort, string)>();

        var events = EtherCatMonitor.DetectChanges(previous, current, DefaultCrcThreshold, crcNotified);

        events.Should().ContainSingle()
            .Which.Should().BeOfType<MasterStateChangedEvent>()
            .Which.Should().BeEquivalentTo(new
            {
                MasterId,
                CurrentState = "Op",
                PreviousState = "Unknown",
            });
    }

    // Fact 5's crash, at the level it could still reach: two slaves sharing a fixed address is a
    // bus misconfiguration rather than a read artifact now, but ToDictionary would still throw
    // ArgumentException for it, escaping DetectChanges and killing the master's poll cycle.
    [Fact]
    public void DetectChanges_does_not_throw_when_two_slaves_share_a_physical_address()
    {
        var previous = CreateSnapshot(slaves:
        [
            CreateSlave(address: 1001, name: "EL1008", currentState: "OP"),
            CreateSlave(address: 1001, name: "EL1008", currentState: "OP"),
        ]);
        var current = CreateSnapshot(slaves:
        [
            CreateSlave(address: 1001, name: "EL1008", currentState: "SAFEOP"),
            CreateSlave(address: 1001, name: "EL1008", currentState: "SAFEOP"),
        ]);
        var crcNotified = new HashSet<(int, ushort, string)>();

        var act = () => EtherCatMonitor.DetectChanges(
            previous, current, DefaultCrcThreshold, crcNotified);

        act.Should().NotThrow<ArgumentException>();
        act().OfType<SlaveStateChangedEvent>().Should().NotBeEmpty();
    }

    // Pins the appearance/disappearance asymmetry explicitly, because a deleted comment used to
    // claim the opposite: a suppressed disappearance batch was said to be followed by a
    // "mirror-image reappeared batch", and no such batch exists — a slave present in current but
    // absent from previous emits nothing.
    [Fact]
    public void DetectChanges_emits_nothing_for_a_slave_that_appears_for_the_first_time()
    {
        var previous = CreateSnapshot(slaves: []);
        var current = CreateSnapshot(slaves: [CreateSlave(address: 1001, name: "EL1008")]);
        var crcNotified = new HashSet<(int, ushort, string)>();

        var events = EtherCatMonitor.DetectChanges(previous, current, DefaultCrcThreshold, crcNotified);

        events.Should().BeEmpty();
    }

    [Fact]
    public void DetectChanges_emits_CrcErrorThresholdExceeded_when_threshold_crossed()
    {
        var portErrors = new List<PortErrorCounters>
        {
            new() { Port = "A", CrcErrors = 150, ForwardedCrcErrors = 0, LostLinkCount = 0 },
        };
        var previous = CreateSnapshot(slaves: [CreateSlave()]);
        var current = CreateSnapshot(slaves: [CreateSlave(portErrors: portErrors)]);
        var crcNotified = new HashSet<(int, ushort, string)>();

        var events = EtherCatMonitor.DetectChanges(previous, current, DefaultCrcThreshold, crcNotified);

        var crcEvent = events.OfType<CrcErrorThresholdExceededEvent>().Should().ContainSingle().Which;
        crcEvent.Should().BeEquivalentTo(new
        {
            MasterId,
            Address = (ushort)1001,
            Port = "A",
            CrcCount = 150,
            Threshold = DefaultCrcThreshold,
        });
        crcNotified.Should().Contain((MasterId, (ushort)1001, "A"));
    }

    [Fact]
    public void DetectChanges_does_not_re_emit_CrcError_if_already_notified()
    {
        var portErrors = new List<PortErrorCounters>
        {
            new() { Port = "A", CrcErrors = 200, ForwardedCrcErrors = 0, LostLinkCount = 0 },
        };
        var previous = CreateSnapshot(slaves: [CreateSlave()]);
        var current = CreateSnapshot(slaves: [CreateSlave(portErrors: portErrors)]);
        var crcNotified = new HashSet<(int, ushort, string)> { (MasterId, 1001, "A") };

        var events = EtherCatMonitor.DetectChanges(previous, current, DefaultCrcThreshold, crcNotified);

        events.OfType<CrcErrorThresholdExceededEvent>().Should().BeEmpty();
    }

    [Fact]
    public void DetectChanges_re_emits_CrcError_after_notified_set_cleared()
    {
        var portErrors = new List<PortErrorCounters>
        {
            new() { Port = "A", CrcErrors = 150, ForwardedCrcErrors = 0, LostLinkCount = 0 },
        };
        var previous = CreateSnapshot(slaves: [CreateSlave()]);
        var current = CreateSnapshot(slaves: [CreateSlave(portErrors: portErrors)]);
        var crcNotified = new HashSet<(int, ushort, string)> { (MasterId, 1001, "A") };

        // Simulates the clearing rather than performing it. That ClearCrcNotification really
        // produces this state is what the four tests below hold — see their shared comment.
        crcNotified.Clear();

        var events = EtherCatMonitor.DetectChanges(previous, current, DefaultCrcThreshold, crcNotified);

        events.OfType<CrcErrorThresholdExceededEvent>().Should().ContainSingle();
    }

    // ── ClearCrcNotification ──────────────────────────────────────────────────
    //
    // The other half of the one-shot CRC alarm, and the half that used to be asserted from
    // Adsify.Tests: EtherCatServiceTests seeded the monitor's tracking set and re-read it to prove
    // that resetting a slave's error counters re-arms its alarm. Those tests now assert only that
    // EtherCatService MAKES the call — correctly, because whether the call clears anything is the
    // monitor's contract, not the service's. This is where that contract lives, so the behaviour
    // moved here rather than disappearing when the monitor moved to the library.
    //
    // Pinning it matters because the alarm is one-shot per port: a clear that silently did nothing
    // would leave a slave permanently unable to re-alarm after its counters were reset, and the
    // DetectChanges test above would stay green throughout, since it simulates the cleared set
    // rather than producing it.

    /// <summary>
    /// A monitor built only to exercise the CRC tracking set. Every collaborator is inert: these
    /// tests never poll, so nothing below is reached.
    /// </summary>
    private static EtherCatMonitor CreateMonitor() => new(
        Substitute.For<IEtherCatClient>(),
        Substitute.For<IEtherCatCache>(),
        Substitute.For<IEtherCatDiagnosticsHandler>(),
        Substitute.For<IEtherCatOptionsSource>(),
        Options.Create(new TwinCatAdsOptions()),
        NullLogger<EtherCatMonitor>.Instance);

    [Fact]
    public void ClearCrcNotification_re_arms_the_alarm_for_the_slave_it_names()
    {
        var monitor = CreateMonitor();
        monitor.MarkCrcNotified(MasterId, 1001, "A");
        monitor.IsCrcNotified(MasterId, 1001, "A").Should().BeTrue(
            "the fixture has to start from a notified port for the clear to mean anything");

        monitor.ClearCrcNotification(MasterId, 1001);

        monitor.IsCrcNotified(MasterId, 1001, "A").Should().BeFalse();
    }

    // ClearCrcNotification names a slave, not a port, and that is deliberate: it is called after
    // ResetSlaveErrorCountersAsync, which resets every counter on the slave. Re-arming only one
    // port would leave the others permanently silent despite their counters having been zeroed.
    [Fact]
    public void ClearCrcNotification_re_arms_every_port_on_the_slave_it_names()
    {
        var monitor = CreateMonitor();
        monitor.MarkCrcNotified(MasterId, 1001, "A");
        monitor.MarkCrcNotified(MasterId, 1001, "D");

        monitor.ClearCrcNotification(MasterId, 1001);

        monitor.IsCrcNotified(MasterId, 1001, "A").Should().BeFalse();
        monitor.IsCrcNotified(MasterId, 1001, "D").Should().BeFalse();
    }

    // Resetting one slave's counters says nothing about any other slave's, on this master or any
    // other. A clear that swept more than it was asked to would replay alarms for bus conditions
    // that never changed — the same fault the one-shot behaviour exists to avoid.
    [Fact]
    public void ClearCrcNotification_leaves_other_slaves_and_other_masters_notified()
    {
        var monitor = CreateMonitor();
        monitor.MarkCrcNotified(MasterId, 1001, "A");
        monitor.MarkCrcNotified(MasterId, 1002, "A");
        monitor.MarkCrcNotified(MasterId + 1, 1001, "A");

        monitor.ClearCrcNotification(MasterId, 1001);

        monitor.IsCrcNotified(MasterId, 1001, "A").Should().BeFalse();
        monitor.IsCrcNotified(MasterId, 1002, "A").Should().BeTrue(
            "another slave on the same master keeps its alarm");
        monitor.IsCrcNotified(MasterId + 1, 1001, "A").Should().BeTrue(
            "the same address on a different master is a different slave");
    }

    // Reachable in production: the REST reset endpoint clears unconditionally on a successful
    // reset, and a slave whose counters were never above the threshold has nothing marked.
    [Fact]
    public void ClearCrcNotification_is_harmless_for_a_slave_that_was_never_notified()
    {
        var monitor = CreateMonitor();

        var act = () => monitor.ClearCrcNotification(MasterId, 1001);

        act.Should().NotThrow();
        monitor.IsCrcNotified(MasterId, 1001, "A").Should().BeFalse();
    }

    [Fact]
    public void DetectChanges_emits_SyncUnitFault_when_hasError_changes()
    {
        var prevSyncUnits = new List<SyncUnitInfo>
        {
            new() { Id = 1, HasError = false, FaultCounter = 0, FramesMissed = 0, Slaves = [1001] },
        };
        var curSyncUnits = new List<SyncUnitInfo>
        {
            new() { Id = 1, HasError = true, FaultCounter = 3, FramesMissed = 10, Slaves = [1001] },
        };
        var previous = CreateSnapshot(syncUnits: prevSyncUnits);
        var current = CreateSnapshot(syncUnits: curSyncUnits);
        var crcNotified = new HashSet<(int, ushort, string)>();

        var events = EtherCatMonitor.DetectChanges(previous, current, DefaultCrcThreshold, crcNotified);

        events.Should().ContainSingle()
            .Which.Should().BeOfType<SyncUnitFaultEvent>()
            .Which.Should().BeEquivalentTo(new
            {
                MasterId,
                SyncUnitId = 1,
                HasError = true,
                FaultCounter = 3L,
            });
    }
}
