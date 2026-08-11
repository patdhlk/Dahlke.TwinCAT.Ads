using Dahlke.EtherCAT.Cia402;
using Dahlke.EtherCAT.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TwinCAT.Ads;

namespace Dahlke.EtherCAT.Diagnostics.Tests;

/// <summary>
/// The CiA-402 statusword read (issue #74).
///
/// The two harnesses split the same way <see cref="EtherCatClientCoeWriteTests"/> splits them:
/// <see cref="SimulatedRawChannelFixture"/> answers "does a seeded statusword come back decoded",
/// and <see cref="ScriptedRawChannelFactory"/> answers what the client does with an answer the
/// simulation cannot produce — a slave's own abort, an absent mailbox, or a reply too short to be a
/// statusword.
///
/// The statuswords are the ones read off the Bonfiglioli ACU during the commissioning that prompted
/// the issue, so the decode is checked against words a real drive actually sent.
/// </summary>
public class EtherCatClientCia402Tests
{
    private const uint IgCoeSdo = 0xF302;
    private const ushort DriveAddress = 1004;

    /// <summary>0x6041 subindex 0, as an ADS index offset — the statusword.</summary>
    private const uint StatuswordOffset = 0x60410000;

    /// <summary>Attempt to read a write-only object, which is a real answer a read can get.</summary>
    private const uint AbortReadOfWriteOnly = 0x06010001;

    private static EtherCatClient ClientOver(ScriptedRawChannelFactory factory) =>
        new(NullLogger<EtherCatClient>.Instance, factory);

    [Fact]
    public async Task ReadCia402StatusAsync_decodes_a_statusword_the_drive_answers_with()
    {
        using var fixture = new SimulatedRawChannelFixture();
        fixture.Seed(DriveAddress, IgCoeSdo, StatuswordOffset, SimulatedRawChannelFixture.U16(0x0637));

        var result = await fixture.Client.ReadCia402StatusAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, timeoutMs: 1000,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Reason.Should().Be(CoeFailureReason.None);
        result.Statusword.Should().Be(0x0637);
        result.Error.Should().BeNull();

        result.Status.Should().NotBeNull();

        var status = result.Status!.Value;
        status.State.Should().Be(Cia402State.OperationEnabled);
        status.VoltageEnabled.Should().BeTrue();
        status.Remote.Should().BeTrue();
        status.TargetReached.Should().BeTrue();
        status.Warning.Should().BeFalse();
        status.InternalLimitActive.Should().BeFalse();
    }

    [Fact]
    public async Task ReadCia402StatusAsync_reads_0x6041_on_the_slaves_own_ads_port()
    {
        // Same addressing trap the CoE read and write carry: port 0xFFFF would answer IG 0xF302 from
        // the MASTER's object dictionary, so a statusword read routed there would report a state
        // belonging to no drive at all.
        var factory = new ScriptedRawChannelFactory();

        await ClientOver(factory).ReadCia402StatusAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, timeoutMs: 1500,
            CancellationToken.None);

        var call = factory.Reads.Should().ContainSingle().Subject;
        call.AmsNetId.Should().Be(SimulatedRawChannelFixture.MasterNetId);
        call.Port.Should().Be(DriveAddress);
        call.IndexGroup.Should().Be(IgCoeSdo);
        call.IndexOffset.Should().Be(StatuswordOffset);
        call.Data.Should().HaveCount(2, "a statusword is a UINT16 and nothing more is asked for");
        call.Timeout.Should().Be(TimeSpan.FromMilliseconds(1500),
            "the caller's own timeout bounds this attempt, as it does the CoE read this delegates to");
    }

    [Fact]
    public async Task ReadCia402StatusAsync_refuses_to_decode_an_answer_too_short_to_be_a_statusword()
    {
        // The one failure this method has that the CoE read does not: ReadCoeObjectAsync reports a
        // short answer as a SUCCESS with fewer bytes, and a single byte is not a statusword. Decoding
        // it would invent the high byte — which carries remote and target-reached — and produce a
        // plausible-looking state the drive never reported.
        //
        // ScriptedRawChannelFactory with nothing to fail with answers zero bytes, which is that case.
        var factory = new ScriptedRawChannelFactory();

        var result = await ClientOver(factory).ReadCia402StatusAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, timeoutMs: 1000,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Status.Should().BeNull();
        result.Statusword.Should().BeNull();
        result.Reason.Should().Be(CoeFailureReason.AdsError);
        result.Error.Should().Contain("0x6041").And.Contain("expected 2");
    }

    [Fact]
    public async Task ReadCia402StatusAsync_reports_an_unknown_word_as_a_successful_read()
    {
        // 0x1591 came off the real drive and matches no row of the state table. That is the DRIVE
        // saying something the standard does not name, not a failed read, and collapsing the two
        // would hide a live statusword behind an error.
        using var fixture = new SimulatedRawChannelFixture();
        fixture.Seed(DriveAddress, IgCoeSdo, StatuswordOffset, SimulatedRawChannelFixture.U16(0x1591));

        var result = await fixture.Client.ReadCia402StatusAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, timeoutMs: 1000,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Statusword.Should().Be(0x1591, "the raw word is the only thing worth having here");
        result.Status!.Value.State.Should().Be(Cia402State.Unknown);
        result.Status!.Value.Warning.Should().BeTrue("bit 7 decodes whether or not the state is named");
    }

    [Fact]
    public async Task ReadCia402StatusAsync_reports_a_slave_without_0x6041_as_ObjectNotFound()
    {
        // Seeded nothing: the simulation answers an unseeded slot with DeviceInvalidOffset, the code
        // real hardware gives for an object a mailbox-bearing slave does not have. This is what a
        // non-drive with a mailbox looks like.
        using var fixture = new SimulatedRawChannelFixture();

        var result = await fixture.Client.ReadCia402StatusAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, timeoutMs: 1000,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(CoeFailureReason.ObjectNotFound);
        result.Status.Should().BeNull();
    }

    [Fact]
    public async Task ReadCia402StatusAsync_reports_a_mailbox_less_slave_as_NoMailbox()
    {
        var factory = new ScriptedRawChannelFactory(
            failWith: new AdsErrorException("no port", AdsErrorCode.PortNotConnected));

        var result = await ClientOver(factory).ReadCia402StatusAsync(
            SimulatedRawChannelFixture.MasterNetId, 1001, timeoutMs: 1000, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(CoeFailureReason.NoMailbox);
        result.AbortCode.Should().BeNull();
        result.Error.Should().Be(nameof(AdsErrorCode.PortNotConnected));
    }

    [Fact]
    public async Task ReadCia402StatusAsync_forwards_the_slaves_own_abort_code()
    {
        // The whole reason this returns a result rather than a nullable status: the drive's abort is
        // the diagnosis, and it has to survive the extra layer this method adds over the CoE read.
        var factory = new ScriptedRawChannelFactory(
            failWith: new AdsErrorException("aborted", (AdsErrorCode)AbortReadOfWriteOnly));

        var result = await ClientOver(factory).ReadCia402StatusAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, timeoutMs: 1000,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(CoeFailureReason.SdoAbort);
        result.AbortCode.Should().Be(AbortReadOfWriteOnly);
        result.Error.Should().Contain("0x06010001");
    }

    [Fact]
    public async Task ReadCia402StatusAsync_reports_a_timeout_as_NoMailbox()
    {
        var factory = new ScriptedRawChannelFactory(failWith: new TimeoutException());

        var result = await ClientOver(factory).ReadCia402StatusAsync(
            SimulatedRawChannelFixture.MasterNetId, 1001, timeoutMs: 1000, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(CoeFailureReason.NoMailbox);
        result.Error.Should().Be("Timeout");
    }

    [Fact]
    public async Task ReadCia402StatusAsync_propagates_caller_cancellation()
    {
        using var fixture = new SimulatedRawChannelFixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await fixture.Client.ReadCia402StatusAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, timeoutMs: 1000, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(0x0231, Cia402State.ReadyToSwitchOn)]
    [InlineData(0x0237, Cia402State.OperationEnabled)]
    [InlineData(0x0250, Cia402State.SwitchOnDisabled)]
    [InlineData(0x0218, Cia402State.Fault)]
    public async Task ReadCia402StatusAsync_decodes_the_words_that_were_decoded_by_hand(
        int statusword, Cia402State expected)
    {
        // End to end over the real raw-channel plumbing, for the words the commissioning session
        // decoded on paper. The decoder has its own exhaustive tests; this checks the bytes survive
        // the trip and are read little-endian.
        using var fixture = new SimulatedRawChannelFixture();
        fixture.Seed(DriveAddress, IgCoeSdo, StatuswordOffset,
            SimulatedRawChannelFixture.U16((ushort)statusword));

        var result = await fixture.Client.ReadCia402StatusAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, timeoutMs: 1000,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Statusword.Should().Be((ushort)statusword);
        result.Status!.Value.State.Should().Be(expected);
    }
}
