using Microsoft.Extensions.Logging.Abstractions;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Pins the cancel-on-dispose contract of <see cref="OperationBound"/> and its use in
/// <see cref="AdsRawChannel"/>: the token a completed call ran under must be CANCELLED, not
/// merely disposed, once the call scope exits. Beckhoff's <c>AdsClientServer.RequestAsync</c>
/// races every request against <c>Task.Delay(AdsClient.Timeout, token)</c> and abandons the
/// delay when the request wins, so an uncancelled token keeps a timer — and everything it
/// roots — armed for the full client backstop (an hour on the raw path). This is the leak that
/// froze a memory-constrained embedded host in production (2026-08-27); these tests are its
/// regression pin.
/// </summary>
public class OperationBoundTests
{
    [Fact]
    public void Dispose_CancelsTheLinkedToken()
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        var bound = new OperationBound(linked);
        var token = bound.Token;

        Assert.False(token.IsCancellationRequested);
        bound.Dispose();
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Dispose_DisposesTheTimerSource()
    {
        var timer = new CancellationTokenSource(TimeSpan.FromHours(1));
        var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token);

        new OperationBound(linked, timer).Dispose();

        // A disposed source refuses CancelAfter — the observable proof Dispose reached it.
        Assert.Throws<ObjectDisposedException>(() => timer.CancelAfter(1000));
    }

    /// <summary>
    /// Records the token each operation runs under, so the test can observe what a
    /// third-party racing that token (Beckhoff's abandoned <c>Task.Delay</c>) would observe.
    /// </summary>
    private sealed class TokenCapturingConnection : IManagedRawConnection
    {
        public CancellationToken Captured { get; private set; }
        public bool IsConnected { get; private set; }
        public void Connect() => IsConnected = true;

        public Task<int> ReadAsync(uint ig, uint io, Memory<byte> destination, CancellationToken ct)
        {
            Captured = ct;
            return Task.FromResult(0);
        }

        public Task WriteAsync(uint ig, uint io, ReadOnlyMemory<byte> source, CancellationToken ct)
        {
            Captured = ct;
            return Task.CompletedTask;
        }

        public Task<int> ReadWriteAsync(
            uint ig, uint io, Memory<byte> destination, ReadOnlyMemory<byte> source, CancellationToken ct)
        {
            Captured = ct;
            return Task.FromResult(0);
        }

        public Task<StateInfo> ReadStateAsync(CancellationToken ct)
        {
            Captured = ct;
            return Task.FromResult(new StateInfo(AdsState.Run, (ushort)0));
        }

        public Task<uint> AddNotificationAsync(
            uint ig, uint io, int length, int cycleTimeMs,
            Action<ReadOnlyMemory<byte>> onData, CancellationToken ct)
        {
            Captured = ct;
            return Task.FromResult(1u);
        }

        public Task RemoveNotificationAsync(uint handle, CancellationToken ct) => Task.CompletedTask;

        public void Dispose() { }
    }

    [Fact]
    public async Task RawRead_Completing_CancelsThePerCallTokenItRanUnder()
    {
        var transport = new TokenCapturingConnection();
        var channel = new AdsRawChannel(
            "1.2.3.4.5.6", 0xFFFF, (_, _) => transport,
            new AdsRawChannelOptions(), NullLogger.Instance, TimeProvider.System);

        await channel.ReadAsync(0x11, 1, new byte[1], CancellationToken.None);

        Assert.True(transport.Captured.IsCancellationRequested);
    }

    [Fact]
    public async Task RawReadState_Completing_CancelsThePerCallTokenItRanUnder()
    {
        var transport = new TokenCapturingConnection();
        var channel = new AdsRawChannel(
            "1.2.3.4.5.6", 0xFFFF, (_, _) => transport,
            new AdsRawChannelOptions(), NullLogger.Instance, TimeProvider.System);

        await channel.ReadStateAsync(CancellationToken.None);

        Assert.True(transport.Captured.IsCancellationRequested);
    }

    [Fact]
    public async Task RawSubscribe_Completing_CancelsThePerCallTokenItRanUnder()
    {
        var transport = new TokenCapturingConnection();
        var channel = new AdsRawChannel(
            "1.2.3.4.5.6", 0xFFFF, (_, _) => transport,
            new AdsRawChannelOptions(), NullLogger.Instance, TimeProvider.System);

        using var subscription = await channel.SubscribeAsync(
            0x11, 1, 1, 100, _ => { }, CancellationToken.None);

        Assert.True(transport.Captured.IsCancellationRequested);
    }
}
