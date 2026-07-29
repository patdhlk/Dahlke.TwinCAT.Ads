using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Tests;

public class SimulatedRawConnectionTests
{
    private static (SimulatedRawConnection Connection, SimulatedRawStore Store) Create()
    {
        var store = new SimulatedRawStore();
        var connection = new SimulatedRawConnection("1.2.3.4.5.6", 0xFFFF, store);
        connection.Connect();
        return (connection, store);
    }

    [Fact]
    public async Task Read_OfUnseededSlot_ThrowsDeviceInvalidOffset()
    {
        var (connection, _) = Create();

        var ex = await Assert.ThrowsAsync<AdsErrorException>(
            () => connection.ReadAsync(0x11, 1001, new byte[4], CancellationToken.None));

        Assert.Equal(AdsErrorCode.DeviceInvalidOffset, ex.ErrorCode);
    }

    [Fact]
    public async Task Read_OfSeededSlot_ReturnsSeededBytes()
    {
        var (connection, store) = Create();
        store.Seed(0x11, 1001, [0x02, 0x00, 0x00, 0x00]);

        var buffer = new byte[4];
        var read = await connection.ReadAsync(0x11, 1001, buffer, CancellationToken.None);

        Assert.Equal(4, read);
        Assert.Equal([0x02, 0x00, 0x00, 0x00], buffer);
    }

    [Fact]
    public async Task Read_SeededShorterThanDestination_ReturnsSeededLength()
    {
        var (connection, store) = Create();
        store.Seed(0x11, 1001, [0xAA, 0xBB]);

        var buffer = new byte[8];
        var read = await connection.ReadAsync(0x11, 1001, buffer, CancellationToken.None);

        Assert.Equal(2, read);
        Assert.Equal(0xAA, buffer[0]);
        Assert.Equal(0xBB, buffer[1]);
        Assert.Equal(0, buffer[2]);
    }

    [Fact]
    public async Task Read_SeededLongerThanDestination_FillsAndReturnsDestinationLength()
    {
        var (connection, store) = Create();
        store.Seed(0x11, 1001, [1, 2, 3, 4, 5, 6]);

        var buffer = new byte[2];
        var read = await connection.ReadAsync(0x11, 1001, buffer, CancellationToken.None);

        Assert.Equal(2, read);
        Assert.Equal([1, 2], buffer);
    }

    [Fact]
    public async Task Write_CreatesSlotAndRoundTrips()
    {
        var (connection, _) = Create();

        await connection.WriteAsync(0x4020, 7, new byte[] { 9, 9 }, CancellationToken.None);

        var buffer = new byte[2];
        var read = await connection.ReadAsync(0x4020, 7, buffer, CancellationToken.None);
        Assert.Equal(2, read);
        Assert.Equal([9, 9], buffer);
    }

    [Fact]
    public async Task ReadWrite_WritesSourceThenReturnsSlot()
    {
        var (connection, _) = Create();

        var buffer = new byte[3];
        var read = await connection.ReadWriteAsync(
            0x7000, 0, buffer, new byte[] { 7, 8, 9 }, CancellationToken.None);

        Assert.Equal(3, read);
        Assert.Equal([7, 8, 9], buffer);
    }

    [Fact]
    public async Task Subscription_FiresOnWriteToWatchedSlot()
    {
        var (connection, store) = Create();
        store.Seed(0x11, 1001, [0]);

        var received = new List<byte[]>();
        await connection.AddNotificationAsync(
            0x11, 1001, length: 1, cycleTimeMs: 100,
            data => received.Add(data.ToArray()), CancellationToken.None);

        await connection.WriteAsync(0x11, 1001, new byte[] { 42 }, CancellationToken.None);

        Assert.Single(received);
        Assert.Equal([42], received[0]);
    }

    [Fact]
    public async Task Subscription_DoesNotFireForOtherSlots()
    {
        var (connection, _) = Create();

        var fired = false;
        await connection.AddNotificationAsync(
            0x11, 1001, length: 1, cycleTimeMs: 100,
            _ => fired = true, CancellationToken.None);

        await connection.WriteAsync(0x11, 9999, new byte[] { 1 }, CancellationToken.None);

        Assert.False(fired);
    }

    [Fact]
    public async Task RemovedSubscription_StopsFiring()
    {
        var (connection, _) = Create();

        var count = 0;
        var handle = await connection.AddNotificationAsync(
            0x11, 1001, length: 1, cycleTimeMs: 100,
            _ => count++, CancellationToken.None);

        await connection.WriteAsync(0x11, 1001, new byte[] { 1 }, CancellationToken.None);
        await connection.RemoveNotificationAsync(handle, CancellationToken.None);
        await connection.WriteAsync(0x11, 1001, new byte[] { 2 }, CancellationToken.None);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ReadState_ReportsRun()
    {
        var (connection, _) = Create();
        var state = await connection.ReadStateAsync(CancellationToken.None);
        Assert.Equal(AdsState.Run, state.AdsState);
    }

    /// <summary>
    /// A disposed transport must refuse every operation, the way a disposed
    /// <c>AdsClient</c> does. Without this, an evicted connection would keep
    /// serving reads out of the still-live shared store and any test asserting
    /// "an in-flight call is protected from eviction" would pass whether or not
    /// the protection existed.
    /// </summary>
    [Fact]
    public async Task DisposedConnection_RefusesEveryOperation()
    {
        var (connection, store) = Create();
        store.Seed(0x11, 1001, [1]);

        connection.Dispose();

        Assert.False(connection.IsConnected);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => connection.ReadAsync(0x11, 1001, new byte[1], CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => connection.WriteAsync(0x11, 1001, new byte[] { 2 }, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => connection.ReadWriteAsync(0x11, 1001, new byte[1], new byte[] { 3 }, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => connection.ReadStateAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => connection.AddNotificationAsync(
                0x11, 1001, length: 1, cycleTimeMs: 100, _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => connection.RemoveNotificationAsync(1, CancellationToken.None));

        // The store itself is untouched: it outlives every transport that wraps it.
        var fresh = new SimulatedRawConnection("1.2.3.4.5.6", 0xFFFF, store);
        var buffer = new byte[1];
        Assert.Equal(1, await fresh.ReadAsync(0x11, 1001, buffer, CancellationToken.None));
        Assert.Equal(1, buffer[0]);
    }
}
