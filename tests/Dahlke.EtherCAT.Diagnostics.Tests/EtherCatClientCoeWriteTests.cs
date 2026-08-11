using Dahlke.EtherCAT.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TwinCAT.Ads;

namespace Dahlke.EtherCAT.Diagnostics.Tests;

/// <summary>
/// Exercises the CoE SDO download path (issue #73).
///
/// Two harnesses, for two different questions. <see cref="SimulatedRawChannelFixture"/> answers
/// "does the write land where a read of the same object finds it" — the round trip, through the
/// library's real raw-channel plumbing. <see cref="ScriptedRawChannelFactory"/> answers "what does
/// the client do with the answer the slave gave", which the simulation cannot ask: its
/// <c>WriteAsync</c> always succeeds and it never produces a slave's own abort code.
///
/// The parameter values are the ones a drive commissioning actually writes: index 0x8010 subindex
/// 0x11 is an EL7047 current limit, and 0x2001-range objects stand in for the Bonfiglioli ACU
/// parameters in the issue.
/// </summary>
public class EtherCatClientCoeWriteTests
{
    private const uint IgCoeSdo = 0xF302;
    private const ushort DriveAddress = 1004;

    /// <summary>Attempt to write a read-only object — ETG.1000-6 / CiA 301.</summary>
    private const uint AbortWriteToReadOnly = 0x06010002;

    /// <summary>Value range of parameter exceeded.</summary>
    private const uint AbortValueRange = 0x06090030;

    /// <summary>Data cannot be stored because of local control — a drive in local mode.</summary>
    private const uint AbortLocalControl = 0x08000021;

    private static EtherCatClient ClientOver(ScriptedRawChannelFactory factory) =>
        new(NullLogger<EtherCatClient>.Instance, factory);

    [Fact]
    public async Task WriteCoeObjectAsync_writes_an_object_a_read_of_the_same_object_answers_with()
    {
        using var fixture = new SimulatedRawChannelFixture();

        var write = await fixture.Client.WriteCoeObjectAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, 0x8010, 0x11,
            SimulatedRawChannelFixture.U16(2500), timeoutMs: 1000, CancellationToken.None);

        write.Succeeded.Should().BeTrue();
        write.Reason.Should().Be(CoeFailureReason.None);
        write.AbortCode.Should().BeNull();
        write.Error.Should().BeNull();

        // The acceptance criterion is a write followed by a read-back, so read it back — through
        // the sibling method, at the same object, on the same port.
        var read = await fixture.Client.ReadCoeObjectAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, 0x8010, 0x11,
            timeoutMs: 1000, maxBytes: 2, CancellationToken.None);

        read.Succeeded.Should().BeTrue();
        read.Data.Should().Equal(SimulatedRawChannelFixture.U16(2500));
    }

    [Fact]
    public async Task WriteCoeObjectAsync_addresses_the_slave_by_ads_port_at_the_CoE_index_group()
    {
        // The read path's own doc records what happens when this is got wrong: port 0xFFFF answers
        // IG 0xF302 from the MASTER's object dictionary, so a write routed there would silently
        // parameterise the wrong device. Pinned on the write for the same reason.
        var factory = new ScriptedRawChannelFactory();

        await ClientOver(factory).WriteCoeObjectAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, 0x2001, 0x03,
            new byte[] { 0xC4, 0x09 }, timeoutMs: 1500, CancellationToken.None);

        var call = factory.Writes.Should().ContainSingle().Subject;
        call.AmsNetId.Should().Be(SimulatedRawChannelFixture.MasterNetId);
        call.Port.Should().Be(DriveAddress);
        call.IndexGroup.Should().Be(IgCoeSdo);
        call.IndexOffset.Should().Be(0x20010003);
        call.Data.Should().Equal(new byte[] { 0xC4, 0x09 });
        call.Timeout.Should().Be(TimeSpan.FromMilliseconds(1500),
            "the caller's own timeout bounds this attempt, not DefaultTimeout");
    }

    [Fact]
    public async Task WriteCoeObjectAsync_surfaces_a_read_only_abort_instead_of_a_generic_failure()
    {
        // What the issue is about: 0x06010002 reaching a caller as CoeFailureReason.AdsError with
        // Error "100728834" is the abort being swallowed. It has to arrive named and numbered.
        var factory = new ScriptedRawChannelFactory(
            failWith: new AdsErrorException("aborted", (AdsErrorCode)AbortWriteToReadOnly));

        var result = await ClientOver(factory).WriteCoeObjectAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, 0x1008, 0x00,
            new byte[] { 0x01 }, timeoutMs: 1000, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(CoeFailureReason.SdoAbort);
        result.AbortCode.Should().Be(AbortWriteToReadOnly);
        result.Error.Should().Contain("0x06010002").And.Contain("read only");
    }

    [Theory]
    [InlineData(AbortValueRange, "range")]
    [InlineData(AbortLocalControl, "local control")]
    public async Task WriteCoeObjectAsync_describes_the_aborts_a_drive_answers_with(
        uint abort, string expectedInDescription)
    {
        var factory = new ScriptedRawChannelFactory(
            failWith: new AdsErrorException("aborted", (AdsErrorCode)abort));

        var result = await ClientOver(factory).WriteCoeObjectAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, 0x2001, 0x03,
            new byte[] { 0xFF, 0xFF }, timeoutMs: 1000, CancellationToken.None);

        result.Reason.Should().Be(CoeFailureReason.SdoAbort);
        result.AbortCode.Should().Be(abort);
        result.Error.Should().Contain(expectedInDescription);
    }

    [Fact]
    public async Task WriteCoeObjectAsync_still_reports_an_abort_it_has_no_description_for()
    {
        // A vendor-specific abort in the same space must not be reported as an ADS error just
        // because this library has no text for it — the code IS the diagnosis.
        const uint unlisted = 0x06040099;
        var factory = new ScriptedRawChannelFactory(
            failWith: new AdsErrorException("aborted", (AdsErrorCode)unlisted));

        var result = await ClientOver(factory).WriteCoeObjectAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, 0x2001, 0x03,
            new byte[] { 0x01 }, timeoutMs: 1000, CancellationToken.None);

        result.Reason.Should().Be(CoeFailureReason.SdoAbort);
        result.AbortCode.Should().Be(unlisted);
        result.Error.Should().Contain("0x06040099");
    }

    [Fact]
    public async Task WriteCoeObjectAsync_reports_a_mailbox_less_slave_as_NoMailbox()
    {
        var factory = new ScriptedRawChannelFactory(
            failWith: new AdsErrorException("no port", AdsErrorCode.PortNotConnected));

        var result = await ClientOver(factory).WriteCoeObjectAsync(
            SimulatedRawChannelFixture.MasterNetId, 1001, 0x2001, 0x03,
            new byte[] { 0x01 }, timeoutMs: 1000, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(CoeFailureReason.NoMailbox);
        result.AbortCode.Should().BeNull("PortNotConnected is the router's answer, not the slave's");
        result.Error.Should().Be(nameof(AdsErrorCode.PortNotConnected));
    }

    [Fact]
    public async Task WriteCoeObjectAsync_reports_an_unknown_object_as_ObjectNotFound()
    {
        var factory = new ScriptedRawChannelFactory(
            failWith: new AdsErrorException("bad offset", AdsErrorCode.DeviceInvalidOffset));

        var result = await ClientOver(factory).WriteCoeObjectAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, 0x9999, 0x00,
            new byte[] { 0x01 }, timeoutMs: 1000, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(CoeFailureReason.ObjectNotFound);
    }

    [Fact]
    public async Task WriteCoeObjectAsync_reports_a_timeout_as_NoMailbox()
    {
        // Same rule as the read path: a mailbox-less slave times out before the router learns the
        // port is absent, so a bare timeout is that unchanging hardware fact, not a transport fault.
        var factory = new ScriptedRawChannelFactory(failWith: new TimeoutException());

        var result = await ClientOver(factory).WriteCoeObjectAsync(
            SimulatedRawChannelFixture.MasterNetId, 1001, 0x2001, 0x03,
            new byte[] { 0x01 }, timeoutMs: 1000, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(CoeFailureReason.NoMailbox);
        result.Error.Should().Be("Timeout");
    }

    [Fact]
    public async Task WriteCoeObjectAsync_reports_an_unrecognised_failure_as_AdsError()
    {
        var factory = new ScriptedRawChannelFactory(failWith: new InvalidOperationException("boom"));

        var result = await ClientOver(factory).WriteCoeObjectAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, 0x2001, 0x03,
            new byte[] { 0x01 }, timeoutMs: 1000, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(CoeFailureReason.AdsError);
        result.Error.Should().Be(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task WriteCoeObjectAsync_propagates_caller_cancellation()
    {
        // The broad catch that turns any failure into a result must not swallow the caller walking
        // away — the same guard GetMastersAsync carries.
        using var fixture = new SimulatedRawChannelFixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await fixture.Client.WriteCoeObjectAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, 0x2001, 0x03,
            new byte[] { 0x01 }, timeoutMs: 1000, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReadCoeObjectAsync_surfaces_an_abort_code_the_same_way_the_write_does()
    {
        // Classify is shared, so a read that a slave aborts stops being a nameless AdsError too —
        // 0x06010001 (attempt to read a write-only object) is a real answer a read can get.
        const uint abortReadOfWriteOnly = 0x06010001;
        var factory = new ScriptedRawChannelFactory(
            failWith: new AdsErrorException("aborted", (AdsErrorCode)abortReadOfWriteOnly));

        var result = await ClientOver(factory).ReadCoeObjectAsync(
            SimulatedRawChannelFixture.MasterNetId, DriveAddress, 0x2001, 0x03,
            timeoutMs: 1000, maxBytes: 4, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be(CoeFailureReason.SdoAbort);
        result.AbortCode.Should().Be(abortReadOfWriteOnly);
        result.Data.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0x05040000u)] // SDO protocol timed out
    [InlineData(0x06010002u)] // attempt to write a read only object
    [InlineData(0x06020000u)] // the object does not exist in the object dictionary
    [InlineData(0x06070010u)] // data type does not match, length of service parameter does not match
    [InlineData(0x06090030u)] // value range of parameter exceeded
    [InlineData(0x08000021u)] // data cannot be stored because of local control
    public void Classify_names_an_SDO_abort_code_as_SdoAbort(uint abort)
    {
        EtherCatClient.Classify((AdsErrorCode)abort).Should().Be(CoeFailureReason.SdoAbort);
    }

    [Fact]
    public void No_AdsErrorCode_Beckhoff_defines_falls_in_the_SDO_abort_space()
    {
        // THIS is what makes the abort-space rule safe rather than a guess. Abort codes arrive in
        // the same 32-bit field as ADS error codes, so telling them apart depends on the two spaces
        // not overlapping: every ETG.1000-6 abort has high byte 0x05, 0x06 or 0x08 above 0xFFFF,
        // and no member of Beckhoff's own enum does. Measured against TwinCAT.Ads 7.0.292 — pinned
        // here so a future release that DID add such a member fails this test instead of quietly
        // making the client report an ADS error as a slave abort.
        var colliding = Enum.GetValues<AdsErrorCode>()
            .Where(EtherCatClient.IsSdoAbort)
            .ToArray();

        colliding.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AdsErrorCode.DeviceInvalidOffset)]
    [InlineData(AdsErrorCode.PortNotConnected)]
    [InlineData(AdsErrorCode.DeviceTimeOut)]
    [InlineData(AdsErrorCode.NoError)]
    public void IsSdoAbort_rejects_an_ordinary_ADS_error(AdsErrorCode error)
    {
        EtherCatClient.IsSdoAbort(error).Should().BeFalse();
    }
}
