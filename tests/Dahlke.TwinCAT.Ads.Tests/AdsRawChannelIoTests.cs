using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TwinCAT.Ads;
using Dahlke.TwinCAT.Ads.Tests.Fakes;

namespace Dahlke.TwinCAT.Ads.Tests;

public class AdsRawChannelIoTests
{
    private static (AdsRawChannel Channel, InMemoryManagedRawConnection Transport) Create(
        AdsRawChannelOptions? options = null)
    {
        var transport = new InMemoryManagedRawConnection();
        var channel = new AdsRawChannel(
            "1.2.3.4.5.6", 0xFFFF,
            (_, _) => transport,
            options ?? new AdsRawChannelOptions(),
            NullLogger.Instance,
            new FakeTimeProvider());
        return (channel, transport);
    }

    [Fact]
    public void State_IsDisconnected_BeforeFirstOperation()
    {
        var (channel, transport) = Create();

        Assert.Equal(ConnectionState.Disconnected, channel.State);
        Assert.Equal(0, transport.ConnectCount);
    }

    [Fact]
    public async Task FirstOperation_ConnectsLazily()
    {
        var (channel, transport) = Create();
        transport.Seed(0x11, 1001, [1, 2, 3, 4]);

        await channel.ReadAsync(0x11, 1001, new byte[4], CancellationToken.None);

        Assert.Equal(1, transport.ConnectCount);
        Assert.Equal(ConnectionState.Connected, channel.State);
    }

    [Fact]
    public async Task SecondOperation_ReusesTheConnection()
    {
        var (channel, transport) = Create();
        transport.Seed(0x11, 1001, [1, 2, 3, 4]);

        await channel.ReadAsync(0x11, 1001, new byte[4], CancellationToken.None);
        await channel.ReadAsync(0x11, 1001, new byte[4], CancellationToken.None);

        Assert.Equal(1, transport.ConnectCount);
    }

    [Fact]
    public async Task Read_ReturnsBytesActuallyRead()
    {
        var (channel, transport) = Create();
        transport.Seed(0x11, 1001, [0xAA, 0xBB]);

        var buffer = new byte[8];
        var read = await channel.ReadAsync(0x11, 1001, buffer, CancellationToken.None);

        Assert.Equal(2, read);
    }

    [Fact]
    public async Task WriteThenRead_RoundTrips()
    {
        var (channel, _) = Create();

        await channel.WriteAsync(0x4020, 7, new byte[] { 5, 6 }, CancellationToken.None);

        var buffer = new byte[2];
        await channel.ReadAsync(0x4020, 7, buffer, CancellationToken.None);
        Assert.Equal([5, 6], buffer);
    }

    [Fact]
    public async Task ReadWrite_WritesThenReads()
    {
        var (channel, _) = Create();

        var buffer = new byte[3];
        var read = await channel.ReadWriteAsync(
            0x7000, 0, buffer, new byte[] { 7, 8, 9 }, CancellationToken.None);

        Assert.Equal(3, read);
        Assert.Equal([7, 8, 9], buffer);
    }

    [Fact]
    public async Task ReadState_ReturnsStateInfo()
    {
        var (channel, _) = Create();

        var state = await channel.ReadStateAsync(CancellationToken.None);

        Assert.Equal(AdsState.Run, state.AdsState);
    }

    [Fact]
    public async Task DeviceErrorCode_SurfacesAsAdsErrorException_AndDoesNotTearDown()
    {
        var (channel, transport) = Create();
        transport.Seed(0x11, 1001, [1]);
        await channel.ReadAsync(0x11, 1001, new byte[1], CancellationToken.None);

        // An unseeded slot answers DeviceInvalidOffset — an ANSWER, not transport death.
        await Assert.ThrowsAsync<AdsErrorException>(
            () => channel.ReadAsync(0x11, 9999, new byte[1], CancellationToken.None));

        Assert.Equal(1, transport.ConnectCount);   // not re-created
        Assert.False(transport.Disposed);
    }

    [Theory]
    [InlineData(AdsErrorCode.PortNotConnected)]
    [InlineData(AdsErrorCode.TargetPortNotFound)]
    [InlineData(AdsErrorCode.DeviceTimeOut)]
    public async Task MailboxAbsentCodes_AreAnswers_NotTransportDeath(AdsErrorCode code)
    {
        var (channel, transport) = Create();
        transport.Seed(0x11, 1, [1]);
        await channel.ReadAsync(0x11, 1, new byte[1], CancellationToken.None);

        transport.FailNextWith = new AdsErrorException("no mailbox", code);

        var ex = await Assert.ThrowsAsync<AdsErrorException>(
            () => channel.ReadAsync(0x11, 1, new byte[1], CancellationToken.None));

        Assert.Equal(code, ex.ErrorCode);
        Assert.Equal(1, transport.ConnectCount);
        Assert.False(transport.Disposed);
    }

    [Fact]
    public async Task CallerCancellation_ThrowsOperationCanceledWithCallerToken()
    {
        var (channel, transport) = Create();
        using var cts = new CancellationTokenSource();
        transport.StallNext = true;

        var pending = channel.ReadAsync(0x11, 1, new byte[1], cts.Token);
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cts.Token, ex.CancellationToken);

        // Caller cancellation is NOT treated as a failed attempt: it is never
        // retried and the transport is never torn down. Cancelling carries no
        // evidence that the transport is unhealthy, and since the channel is safe
        // for concurrent use, dropping it would make one caller's private decision
        // tear down a transport other callers are actively using.
        Assert.False(transport.Disposed);
        Assert.Equal(1, transport.ConnectCount);
    }

    [Fact]
    public async Task InFlightOperation_IsNotEvictedUnderneath()
    {
        var transport = new InMemoryManagedRawConnection();
        var clock = new FakeTimeProvider();
        var channel = new AdsRawChannel(
            "1.2.3.4.5.6", 0xFFFF,
            (_, _) => transport,
            // A bound far beyond the idle window, so advancing past the idle window
            // below cannot trip the operation's own timeout instead.
            new AdsRawChannelOptions { TimeoutMs = 600_000, RetryCount = 0 },
            NullLogger.Instance,
            clock);

        using var cts = new CancellationTokenSource();
        transport.StallNext = true;
        var pending = channel.ReadAsync(0x11, 1, new byte[1], cts.Token);
        await Task.WhenAny(transport.Stalled, pending);   // the call is genuinely in flight

        // The channel now LOOKS idle — LastUseUtc is stamped on entry and the call
        // has been running longer than the idle window. It is not idle: the sweeper
        // must leave a live call's transport alone.
        clock.Advance(TimeSpan.FromMilliseconds(60_001));

        Assert.False(channel.TryEvictIfIdle(TimeSpan.FromMilliseconds(60_000)));
        Assert.False(transport.Disposed);
        Assert.Equal(ConnectionState.Connected, channel.State);

        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);

        // …and once nothing is in flight, that same sweep does evict — proving the
        // guard above is about the live call, not a broken sweep.
        Assert.True(channel.TryEvictIfIdle(TimeSpan.FromMilliseconds(60_000)));
        Assert.True(transport.Disposed);
    }
}
