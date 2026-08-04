using Dahlke.EtherCAT.Diagnostics;
using FluentAssertions;
using TwinCAT.Ads;

namespace Dahlke.EtherCAT.Diagnostics.Tests;

/// <summary>
/// Exercises EtherCatClient's read paths against the library's seeded raw-channel simulation.
/// Byte layouts and values are the ones observed on the EK1100 + 7-terminal rack: eight slaves
/// at fixed addresses 1001-1008, all in Op, the chained slaves reporting two linked ports and
/// the trailing EL2808 reporting one.
/// </summary>
public class EtherCatClientRawChannelTests
{
    private const uint IgMasterState = 0x03;
    private const uint IgSlaveCount = 0x06;
    private const uint IgSlaveAddresses = 0x07;
    private const uint IgSlaveStates = 0x09;
    private const uint IgSlaveIdentity = 0x11;
    private const uint IgCrcErrors = 0x12;
    private const uint IgFrameCounters = 0x0C;
    private const uint IgCoeSdo = 0xF302;
    private const uint IoZero = 0x0;
    private const uint IoMasterState = 0x100;

    private const uint BeckhoffVendorId = 0x2;
    private const uint Ek1100ProductCode = 0x044C2C52;
    private const uint El2808ProductCode = 0x0AF83052;

    /// <summary>
    /// Builds and seeds a fixture but does not own its lifetime: the fixture escapes via the
    /// return value, so disposal is the caller's job (<c>using var fixture = EightSlaveRack();</c>).
    /// Wrapping the construction here in <c>using</c> would dispose the underlying service
    /// provider — and the raw channel factory it owns — the instant this method returns, before
    /// the caller ever gets to use it.
    /// </summary>
    private static SimulatedRawChannelFixture EightSlaveRack()
    {
        var fixture = new SimulatedRawChannelFixture();

        // IG 0x06 IO 0x0 → uint16 projected slave count.
        fixture.SeedMaster(IgSlaveCount, IoZero, SimulatedRawChannelFixture.U16(8));

        // IG 0x07 IO 0x0 → uint16[] fixed addresses, 1001..1008.
        var addresses = new List<byte>();
        for (ushort address = 1001; address <= 1008; address++)
            addresses.AddRange(SimulatedRawChannelFixture.U16(address));
        fixture.SeedMaster(IgSlaveAddresses, IoZero, [.. addresses]);

        // IG 0x09 IO 0x0 → {deviceState, linkState} per slave. 0x08 = Op, link present.
        var states = new List<byte>();
        for (int i = 0; i < 8; i++)
            states.AddRange([0x08, 0x01]);
        fixture.SeedMaster(IgSlaveStates, IoZero, [.. states]);

        return fixture;
    }

    private static byte[] Identity(uint vendor, uint product, uint revision, uint serial) =>
    [
        .. SimulatedRawChannelFixture.U32(vendor),
        .. SimulatedRawChannelFixture.U32(product),
        .. SimulatedRawChannelFixture.U32(revision),
        .. SimulatedRawChannelFixture.U32(serial),
    ];

    [Fact]
    public async Task GetConfiguredSlavesAsync_reads_addresses_states_and_identities()
    {
        using var fixture = EightSlaveRack();
        fixture.SeedMaster(IgSlaveIdentity, 1001, Identity(BeckhoffVendorId, Ek1100ProductCode, 1, 0));
        fixture.SeedMaster(IgSlaveIdentity, 1008, Identity(BeckhoffVendorId, El2808ProductCode, 1, 0));

        var slaves = await fixture.Client.GetConfiguredSlavesAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        slaves.Should().HaveCount(8);
        slaves[0].PhysicalAddress.Should().Be(1001);
        slaves[0].Type.Should().Be("EK1100");
        slaves[0].CurrentState.Should().Be("Op");
        slaves[0].IsPresent.Should().BeTrue();
        slaves[0].HasError.Should().BeFalse();
        slaves[7].PhysicalAddress.Should().Be(1008);
        slaves[7].Type.Should().Be("EL2808");
    }

    [Fact]
    public async Task GetConfiguredSlavesAsync_names_a_slave_by_address_when_its_identity_is_unreadable()
    {
        // Identities are seeded for nothing, so every IG 0x11 read answers DeviceInvalidOffset.
        // The client must fall back to the address-derived name rather than failing the whole call.
        using var fixture = EightSlaveRack();

        var slaves = await fixture.Client.GetConfiguredSlavesAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        slaves.Should().HaveCount(8);
        slaves[0].Name.Should().Be("Slave 1001");
        slaves[0].Type.Should().Be("Unknown");
    }

    [Fact]
    public async Task GetSlaveDetailAsync_reports_two_linked_ports_for_a_chained_slave()
    {
        using var fixture = EightSlaveRack();
        fixture.SeedMaster(IgSlaveIdentity, 1001, Identity(BeckhoffVendorId, Ek1100ProductCode, 1, 42));
        fixture.SeedMaster(IgSlaveStates, 1001, [0x08, 0x01]);
        // IG 0x12 IO=addr → one uint32 CRC counter per linked port. Eight bytes = two ports.
        fixture.SeedMaster(IgCrcErrors, 1001,
            [.. SimulatedRawChannelFixture.U32(0), .. SimulatedRawChannelFixture.U32(0)]);

        var detail = await fixture.Client.GetSlaveDetailAsync(
            SimulatedRawChannelFixture.MasterNetId, 1001, CancellationToken.None);

        detail.Should().NotBeNull();
        detail!.ConfiguredProductCode.Should().Be(Ek1100ProductCode);
        detail.ConfiguredSerialNumber.Should().Be(42);
        detail.InitError.Should().BeFalse();
        detail.Ports.Where(p => p.LinkState).Should().HaveCount(2);
    }

    [Fact]
    public async Task GetSlaveErrorCountersAsync_emits_one_counter_per_reported_port()
    {
        using var fixture = EightSlaveRack();
        // Four bytes = the chain terminator's single linked port, carrying 7 CRC errors.
        fixture.SeedMaster(IgCrcErrors, 1008, SimulatedRawChannelFixture.U32(7));

        var counters = await fixture.Client.GetSlaveErrorCountersAsync(
            SimulatedRawChannelFixture.MasterNetId, 1008, CancellationToken.None);

        counters.Should().NotBeNull();
        counters!.PhysicalAddress.Should().Be(1008);
        counters.Ports.Should().HaveCount(1);
        counters.Ports[0].Port.Should().Be("A");
        counters.Ports[0].CrcErrors.Should().Be(7);
    }

    // -- degradation signal -------------------------------------------------------
    //
    // These replace GetConfiguredSlavesAsync_returns_empty_when_the_master_answers_nothing, which
    // pinned the behaviour this change exists to remove: a failed read answering [] made "the bus
    // has no slaves" and "the read failed" the same value at the IEtherCatClient boundary. The
    // seam is now null-for-failure, value-for-answer, and both halves are pinned here.

    [Fact]
    public async Task GetConfiguredSlavesAsync_returns_null_when_the_master_answers_nothing()
    {
        // Nothing seeded at all: every read answers DeviceInvalidOffset. The client must report
        // that as unavailable rather than as an empty bus — and must not throw, because the
        // polling loop calls this every cycle.
        using var fixture = new SimulatedRawChannelFixture();

        var slaves = await fixture.Client.GetConfiguredSlavesAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        slaves.Should().BeNull();
    }

    [Fact]
    public async Task GetConfiguredSlavesAsync_returns_empty_when_the_master_reports_zero_slaves()
    {
        // A master that answers the count read with 0 has genuinely no configured slaves. That is
        // a reading, not a failure, and must stay distinguishable from one.
        using var fixture = new SimulatedRawChannelFixture();
        fixture.SeedMaster(IgSlaveCount, IoZero, SimulatedRawChannelFixture.U16(0));

        var slaves = await fixture.Client.GetConfiguredSlavesAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        slaves.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task GetConfiguredSlavesAsync_returns_null_when_the_address_read_fails()
    {
        // The count read succeeds but IG 0x07 is unseeded. This is the shape that used to
        // fabricate addresses 1..N: a full-looking slave list at addresses that are not on the
        // bus, which then read as every real slave having disappeared.
        using var fixture = new SimulatedRawChannelFixture();
        fixture.SeedMaster(IgSlaveCount, IoZero, SimulatedRawChannelFixture.U16(8));

        var slaves = await fixture.Client.GetConfiguredSlavesAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        slaves.Should().BeNull();
    }

    [Fact]
    public async Task GetConfiguredSlavesAsync_returns_null_when_the_address_read_is_short()
    {
        // Six of the eight addresses answered. The trailing two used to stay 0, producing
        // duplicate keys that threw ArgumentException out of DetectChanges and killed the cycle.
        using var fixture = new SimulatedRawChannelFixture();
        fixture.SeedMaster(IgSlaveCount, IoZero, SimulatedRawChannelFixture.U16(8));

        var partial = new List<byte>();
        for (ushort address = 1001; address <= 1006; address++)
            partial.AddRange(SimulatedRawChannelFixture.U16(address));
        fixture.SeedMaster(IgSlaveAddresses, IoZero, [.. partial]);

        var slaves = await fixture.Client.GetConfiguredSlavesAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        slaves.Should().BeNull();
    }

    [Fact]
    public async Task GetConfiguredSlavesAsync_returns_null_when_the_state_read_fails()
    {
        // Count and addresses answered, IG 0x09 unseeded. Every slave used to fall back to
        // deviceState/linkState 0, which decodes to "Unknown", not present and disabled — a
        // fabricated bus-wide fault on a list the master said was fine.
        using var fixture = new SimulatedRawChannelFixture();
        fixture.SeedMaster(IgSlaveCount, IoZero, SimulatedRawChannelFixture.U16(2));
        fixture.SeedMaster(IgSlaveAddresses, IoZero,
            [.. SimulatedRawChannelFixture.U16(1001), .. SimulatedRawChannelFixture.U16(1002)]);

        var slaves = await fixture.Client.GetConfiguredSlavesAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        slaves.Should().BeNull();
    }

    [Fact]
    public async Task GetConfiguredSlavesAsync_returns_null_when_the_state_read_is_short()
    {
        using var fixture = new SimulatedRawChannelFixture();
        fixture.SeedMaster(IgSlaveCount, IoZero, SimulatedRawChannelFixture.U16(2));
        fixture.SeedMaster(IgSlaveAddresses, IoZero,
            [.. SimulatedRawChannelFixture.U16(1001), .. SimulatedRawChannelFixture.U16(1002)]);
        // One slave's worth of state bytes for a two-slave bus.
        fixture.SeedMaster(IgSlaveStates, IoZero, [0x08, 0x01]);

        var slaves = await fixture.Client.GetConfiguredSlavesAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        slaves.Should().BeNull();
    }

    [Fact]
    public async Task GetMasterStateAsync_returns_null_when_the_state_read_fails()
    {
        // IG 0x03 unseeded. This used to answer a non-null state carrying "Unknown", which is why
        // PollMasterAsync's `masterState is null` guard never fired for a dropped read and a total
        // bus loss was silent.
        using var fixture = new SimulatedRawChannelFixture();
        fixture.SeedMaster(IgSlaveCount, IoZero, SimulatedRawChannelFixture.U16(8));

        var state = await fixture.Client.GetMasterStateAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        state.Should().BeNull();
    }

    [Fact]
    public async Task GetMasterStateAsync_returns_null_when_the_slave_count_read_fails()
    {
        using var fixture = new SimulatedRawChannelFixture();
        fixture.SeedMaster(IgMasterState, IoMasterState, SimulatedRawChannelFixture.U16(0x08));

        var state = await fixture.Client.GetMasterStateAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        state.Should().BeNull();
    }

    [Fact]
    public async Task GetMasterStateAsync_reports_a_state_code_outside_the_ETG_set_as_Unknown()
    {
        // Raw state byte 0x00 is not an ETG.1000 state (1/2/3/4/8), but the master DID answer.
        // That has to stay reportable and stay distinct from the read failing, which is the whole
        // reason "Unknown" could not be used as a failure sentinel.
        using var fixture = new SimulatedRawChannelFixture();
        fixture.SeedMaster(IgMasterState, IoMasterState, SimulatedRawChannelFixture.U16(0x00));
        fixture.SeedMaster(IgSlaveCount, IoZero, SimulatedRawChannelFixture.U16(0));

        var state = await fixture.Client.GetMasterStateAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        state.Should().NotBeNull();
        state!.CurrentState.Should().Be("Unknown");
        state.SlaveCount.Should().Be(0);
    }

    [Fact]
    public async Task GetMasterStateAsync_reports_the_state_and_count_the_master_answered()
    {
        using var fixture = EightSlaveRack();
        fixture.SeedMaster(IgMasterState, IoMasterState, SimulatedRawChannelFixture.U16(0x08));

        var state = await fixture.Client.GetMasterStateAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        state.Should().NotBeNull();
        state!.CurrentState.Should().Be("Op");
        state.SlaveCount.Should().Be(8);
    }

    [Fact]
    public async Task GetScannedSlavesAsync_returns_null_when_the_address_read_fails()
    {
        // Same fabrication existed here: an unreadable address list produced identities pinned to
        // invented addresses 1..N.
        using var fixture = new SimulatedRawChannelFixture();
        fixture.SeedMaster(IgSlaveCount, IoZero, SimulatedRawChannelFixture.U16(8));

        var scanned = await fixture.Client.GetScannedSlavesAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        scanned.Should().BeNull();
    }

    [Fact]
    public async Task GetScannedSlavesAsync_reports_the_addresses_the_master_answered()
    {
        using var fixture = EightSlaveRack();
        fixture.SeedMaster(IgSlaveIdentity, 1001, Identity(BeckhoffVendorId, Ek1100ProductCode, 1, 42));

        var scanned = await fixture.Client.GetScannedSlavesAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        scanned.Should().NotBeNull().And.HaveCount(8);
        scanned![0].PhysicalAddress.Should().Be(1001);
        scanned[0].SerialNumber.Should().Be(42);
        scanned[7].PhysicalAddress.Should().Be(1008);
    }

    // #42/Task 7: a failed PER-SLAVE identity read used to zero-fill vendor/product/revision/
    // serial and still list the slave, serving a fabricated all-zero device indistinguishable
    // from a real one. It must now stay listed at its real address — dropping it would read
    // exactly like "not physically on the bus", a different fact entirely — with its identity
    // fields absent instead of zeroed.
    [Fact]
    public async Task GetScannedSlavesAsync_reports_an_address_with_absent_identity_when_its_identity_read_fails()
    {
        using var fixture = EightSlaveRack();
        // 1001's IG 0x11 is deliberately left unseeded; 1002's answers normally, so a healthy
        // entry and a failed one are provably side by side in the same response.
        fixture.SeedMaster(IgSlaveIdentity, 1002, Identity(BeckhoffVendorId, El2808ProductCode, 1, 7));

        var scanned = await fixture.Client.GetScannedSlavesAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        scanned.Should().NotBeNull().And.HaveCount(8,
            "the address is known from IG 0x07 regardless of whether IG 0x11 answered for it");

        var unreadable = scanned!.Single(s => s.PhysicalAddress == 1001);
        unreadable.VendorId.Should().BeNull();
        unreadable.ProductCode.Should().BeNull();
        unreadable.RevisionNumber.Should().BeNull();
        unreadable.SerialNumber.Should().BeNull();

        var healthy = scanned.Single(s => s.PhysicalAddress == 1002);
        healthy.VendorId.Should().Be(BeckhoffVendorId);
        healthy.ProductCode.Should().Be(El2808ProductCode);
        healthy.SerialNumber.Should().Be(7u);
    }

    [Fact]
    public async Task ReadCoeObjectAsync_returns_the_seeded_object_from_a_slave_with_a_mailbox()
    {
        using var fixture = new SimulatedRawChannelFixture();
        // CoE addresses the slave by ADS port, not by IO offset. 0x1008:00 is the device name.
        fixture.Seed(1004, IgCoeSdo, EtherCatClient.CoeOffset(0x1008, 0x00), "EL7047"u8.ToArray());

        var result = await fixture.Client.ReadCoeObjectAsync(
            SimulatedRawChannelFixture.MasterNetId, 1004, 0x1008, 0x00,
            timeoutMs: 1000, maxBytes: 64, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        System.Text.Encoding.ASCII.GetString(result.Data).Should().StartWith("EL7047");
    }

    [Fact]
    public async Task ReadCoeObjectAsync_classifies_an_unknown_object_as_ObjectNotFound()
    {
        // A slave that has a mailbox but no such object answers DeviceInvalidOffset, which the
        // simulation gives for any unseeded slot.
        using var fixture = new SimulatedRawChannelFixture();
        fixture.Seed(1004, IgCoeSdo, EtherCatClient.CoeOffset(0x1008, 0x00), "EL7047"u8.ToArray());

        var result = await fixture.Client.ReadCoeObjectAsync(
            SimulatedRawChannelFixture.MasterNetId, 1004, 0x9999, 0x00,
            timeoutMs: 1000, maxBytes: 64, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(CoeFailureReason.ObjectNotFound);
    }

    [Theory]
    [InlineData(0x1018, 0x02, 0x10180002u)]
    [InlineData(0x1008, 0x00, 0x10080000u)]
    [InlineData(0x1C13, 0x00, 0x1C130000u)]
    [InlineData(0xFFFF, 0xFF, 0xFFFF00FFu)]
    public void CoeOffset_packs_index_high_and_subindex_low(int index, int sub, uint expected)
    {
        EtherCatClient.CoeOffset((ushort)index, (byte)sub).Should().Be(expected);
    }

    [Theory]
    [InlineData(AdsErrorCode.PortNotConnected)]
    [InlineData(AdsErrorCode.TargetPortNotFound)]
    [InlineData(AdsErrorCode.ClientSyncTimeOut)]
    [InlineData(AdsErrorCode.DeviceTimeOut)]
    public void Classify_treats_the_mailbox_less_answers_as_NoMailbox(AdsErrorCode error)
    {
        // The coupler and the plain digital terminals on the rack have no mailbox and answer
        // with one of these. The library never retries an ADS error code, so this costs one
        // round trip either way — this specific call (ReadCoeObjectAsync) never had a retry
        // loop to begin with, even pre-migration, so there is no "two round trips" baseline to
        // compare against here. See EtherCatClient's type doc and DEVELOPMENT.md's RawChannels
        // table for where a retry genuinely was added (and regresses the mailbox-less TIMEOUT
        // case specifically, not this ADS-error-code one).
        EtherCatClient.Classify(error).Should().Be(CoeFailureReason.NoMailbox);
    }

    [Fact]
    public async Task GetMastersAsync_propagates_caller_cancellation()
    {
        // GetMastersAsync's probe loop sits a broad catch (Exception) right next to a linked-CTS
        // timeout, which is exactly the shape that can accidentally swallow the caller's own
        // cancellation. SimulatedRawConnection.ReadStateAsync calls ct.ThrowIfCancellationRequested(),
        // so an already-cancelled token must surface as OperationCanceledException rather than
        // being logged and treated as "this candidate isn't a master".
        using var fixture = new SimulatedRawChannelFixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await fixture.Client.GetMastersAsync(
            SimulatedRawChannelFixture.MasterNetId, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // A failed decorating read must be reportable as absent. Before this change these three
    // answered a fully-populated object with zeroed fields, which the polling service could not
    // tell apart from a healthy reading — see issue #42.
    [Fact]
    public async Task GetFrameStatisticsAsync_reports_null_when_the_counter_read_fails()
    {
        using var fixture = EightSlaveRack();
        // IG 0x0C deliberately not seeded.

        var stats = await fixture.Client.GetFrameStatisticsAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        stats.Should().BeNull();
    }

    // IAdsRawChannel.ReadAsync contracts that a SHORT read is possible, and the IG 0x0C block is
    // one reading of five uint32s — there is no such thing as a partial one. Before this the guard
    // was `< 4` and ParseFrameStatistics filled a 0 in for every counter the answer had not
    // reached, so the 8 bytes below served a real cyclicSendFrames beside three fabricated zeros,
    // with incompleteReads empty saying nothing was missing. The next cycle then computed
    // queuedFramesPerSecond as a delta against that zero and spiked.
    [Fact]
    public async Task GetFrameStatisticsAsync_reports_null_when_the_counter_read_answers_short()
    {
        using var fixture = EightSlaveRack();

        // 8 of the 20 bytes: the system time and cyclicSendFrames only. Deliberately non-zero so a
        // regression cannot pass by coincidence — a partial parse would answer
        // cyclicSendFrames: 4096 with cyclicLostFrames/queuedSendFrames/queuedLostFrames zeroed.
        fixture.SeedMaster(IgFrameCounters, 0,
        [
            .. SimulatedRawChannelFixture.U32(0x11223344),
            .. SimulatedRawChannelFixture.U32(4096),
        ]);

        var stats = await fixture.Client.GetFrameStatisticsAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        stats.Should().BeNull("a block short of all five counters is a failed read, not a reading");
    }

    [Fact]
    public async Task GetFrameStatisticsAsync_parses_the_full_counter_block()
    {
        using var fixture = EightSlaveRack();

        fixture.SeedMaster(IgFrameCounters, 0,
        [
            .. SimulatedRawChannelFixture.U32(0x11223344), // system time — skipped
            .. SimulatedRawChannelFixture.U32(4096),       // cyclic sent
            .. SimulatedRawChannelFixture.U32(7),          // cyclic lost
            .. SimulatedRawChannelFixture.U32(512),        // queued sent
            .. SimulatedRawChannelFixture.U32(3),          // queued lost
        ]);

        var stats = await fixture.Client.GetFrameStatisticsAsync(
            SimulatedRawChannelFixture.MasterNetId, CancellationToken.None);

        stats.Should().NotBeNull();
        stats!.CyclicSendFrames.Should().Be(4096);
        stats.CyclicLostFrames.Should().Be(7);
        stats.QueuedSendFrames.Should().Be(512);
        stats.QueuedLostFrames.Should().Be(3);
        // Not readings: IG 0x0C has no Tx/Rx error counters and adsify reads none elsewhere.
        stats.CyclicTxRxErrors.Should().Be(0);
        stats.QueuedTxRxErrors.Should().Be(0);
    }

    [Fact]
    public async Task GetSlaveDetailAsync_reports_null_when_the_identity_read_fails()
    {
        using var fixture = EightSlaveRack();
        // IG 0x11 for slave 1001 deliberately not seeded; seed the other two reads so the
        // identity read is provably the one that decided the result.
        fixture.SeedMaster(IgSlaveStates, 1001, 0x08, 0x01);
        fixture.SeedMaster(IgCrcErrors, 1001, new byte[8]);

        var detail = await fixture.Client.GetSlaveDetailAsync(
            SimulatedRawChannelFixture.MasterNetId, 1001, CancellationToken.None);

        detail.Should().BeNull();
    }

    [Fact]
    public async Task GetSlaveDetailAsync_reports_null_when_the_port_read_fails()
    {
        using var fixture = EightSlaveRack();
        fixture.SeedMaster(IgSlaveIdentity, 1001, Identity(BeckhoffVendorId, Ek1100ProductCode, 0x00110000, 0));
        fixture.SeedMaster(IgSlaveStates, 1001, 0x08, 0x01);
        // IG 0x12 for slave 1001 deliberately not seeded.

        var detail = await fixture.Client.GetSlaveDetailAsync(
            SimulatedRawChannelFixture.MasterNetId, 1001, CancellationToken.None);

        detail.Should().BeNull();
    }

    [Fact]
    public async Task GetSlaveErrorCountersAsync_reports_null_when_the_crc_read_fails()
    {
        using var fixture = EightSlaveRack();
        // IG 0x12 for slave 1001 deliberately not seeded.

        var counters = await fixture.Client.GetSlaveErrorCountersAsync(
            SimulatedRawChannelFixture.MasterNetId, 1001, CancellationToken.None);

        counters.Should().BeNull();
    }

    // The complement: a read that DID answer still returns a value. Without this the tests above
    // would pass against a method that unconditionally returned null.
    [Fact]
    public async Task GetSlaveDetailAsync_still_answers_when_every_read_succeeds()
    {
        using var fixture = EightSlaveRack();
        fixture.SeedMaster(IgSlaveIdentity, 1001, Identity(BeckhoffVendorId, Ek1100ProductCode, 0x00110000, 0));
        fixture.SeedMaster(IgSlaveStates, 1001, 0x08, 0x01);
        fixture.SeedMaster(IgCrcErrors, 1001, new byte[8]);

        var detail = await fixture.Client.GetSlaveDetailAsync(
            SimulatedRawChannelFixture.MasterNetId, 1001, CancellationToken.None);

        detail.Should().NotBeNull();
        detail!.ConfiguredProductCode.Should().Be(Ek1100ProductCode);
        detail.Ports.Count(p => p.LinkState).Should().Be(2);
    }
}
