namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Pins <see cref="AdsConnectionExtensions.WaitForConnectedAsync"/>. It exists because a
/// standalone (or hosted) start returns before a REAL target's loop is released — the
/// router is still coming up — so the caller's first read would otherwise wait out
/// TimeoutMs and throw.
/// </summary>
public class WaitForConnectedAsyncTests
{
    /// <summary>
    /// A minimal connection whose state the test drives. Derives from AdsConnectionBase so
    /// only identity and state are overridden; every other member keeps its throwing default,
    /// proving the extension touches nothing else.
    /// </summary>
    private sealed class StateDouble : AdsConnectionBase
    {
        public StateDouble(ConnectionState initial)
        {
            if (initial != ConnectionState.Connected)
                SetConnectionState(initial);
        }

        public override string PlcId => "plc1";

        public void MoveTo(ConnectionState state) => SetConnectionState(state);
    }

    [Fact]
    public async Task AlreadyConnected_ReturnsTrueImmediately()
    {
        var conn = new StateDouble(ConnectionState.Connected);

        // A zero timeout proves it never waited.
        Assert.True(await conn.WaitForConnectedAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task ConnectsLater_ReturnsTrue()
    {
        var conn = new StateDouble(ConnectionState.Disconnected);

        var waiting = conn.WaitForConnectedAsync(TimeSpan.FromSeconds(30));
        conn.MoveTo(ConnectionState.Connecting);
        conn.MoveTo(ConnectionState.Connected);

        Assert.True(await waiting);
    }

    [Fact]
    public async Task NeverConnects_ReturnsFalseOnTimeout()
    {
        var conn = new StateDouble(ConnectionState.Disconnected);

        Assert.False(await conn.WaitForConnectedAsync(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task IntermediateStates_DoNotSatisfyTheWait()
    {
        var conn = new StateDouble(ConnectionState.Disconnected);

        var waiting = conn.WaitForConnectedAsync(TimeSpan.FromMilliseconds(100));
        conn.MoveTo(ConnectionState.Connecting);

        Assert.False(await waiting);
    }

    [Fact]
    public async Task Cancellation_Throws()
    {
        var conn = new StateDouble(ConnectionState.Disconnected);
        using var cts = new CancellationTokenSource();

        var waiting = conn.WaitForConnectedAsync(TimeSpan.FromSeconds(30), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task NullConnection_Throws()
    {
        IAdsConnection? conn = null;
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => conn!.WaitForConnectedAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task RepeatedWaits_EachSettleIndependently()
    {
        var conn = new StateDouble(ConnectionState.Disconnected);

        Assert.False(await conn.WaitForConnectedAsync(TimeSpan.FromMilliseconds(20)));

        var waiting = conn.WaitForConnectedAsync(TimeSpan.FromSeconds(30));
        conn.MoveTo(ConnectionState.Connected);
        Assert.True(await waiting);
    }
}
