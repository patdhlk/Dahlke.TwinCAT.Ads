using System.Reactive.Linq;
using Dahlke.TwinCAT.Ads.Reactive;
using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dahlke.TwinCAT.Ads.Tests;

public class ReactiveExtensionsTests
{
    private static SimulatedAdsConnection NewSim()
        => new("plc1", "PLC 1", NullLoggerFactory.Instance);

    [Fact]
    public async Task ObserveValue_Typed_EmitsOnChange()
    {
        using var conn = NewSim();
        var received = new List<AdsValueChange<float>>();

        using var sub = conn.ObserveValue<float>("GVL.Temp", cycleTimeMs: 100)
            .Subscribe(received.Add);

        await conn.WriteValueAsync<float>("GVL.Temp", 21.5f, CancellationToken.None);
        await conn.WriteValueAsync<float>("GVL.Temp", 22.5f, CancellationToken.None);

        Assert.Equal(
            [new AdsValueChange<float>("GVL.Temp", 21.5f),
             new AdsValueChange<float>("GVL.Temp", 22.5f)],
            received);
    }

    [Fact]
    public async Task ObserveValue_Untyped_EmitsBoxedValue()
    {
        using var conn = NewSim();
        var received = new List<AdsValueChange<object?>>();

        using var sub = conn.ObserveValue("GVL.Counter", cycleTimeMs: 100)
            .Subscribe(received.Add);

        await conn.WriteValueAsync("GVL.Counter", (short)7, CancellationToken.None);

        var change = Assert.Single(received);
        Assert.Equal("GVL.Counter", change.Symbol);
        Assert.Equal((short)7, change.Value);
    }

    [Fact]
    public async Task ObserveValue_Dispose_DeletesUnderlyingNotification()
    {
        var conn = new FakeManagedConnection("plc1") { IsConnected = true };

        var sub = conn.ObserveValue("MAIN.x", cycleTimeMs: 100).Subscribe(_ => { });
        await conn.SubscribeCalled.WaitAsync(TimeSpan.FromSeconds(5));

        var record = Assert.Single(conn.Subscriptions);
        Assert.False(record.IsDisposed);

        sub.Dispose();
        Assert.True(record.IsDisposed);
    }

    [Fact]
    public async Task ObserveValue_FailedSubscribe_SurfacesOnError()
    {
        var conn = new FakeManagedConnection("plc1") { IsConnected = true };
        conn.SubscribeThrowsOnce = new InvalidOperationException("symbol not found");

        Exception? error = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var sub = conn.ObserveValue("MAIN.missing", cycleTimeMs: 100)
            .Subscribe(_ => { }, ex => { error = ex; done.TrySetResult(); });

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("symbol not found", error!.Message);
    }

    [Fact]
    public void ObserveConnectionState_EmitsRaisedEvents()
    {
        var conn = new FakeManagedConnection("plc1");
        var received = new List<ConnectionStateChangedEventArgs>();

        using var sub = conn.ObserveConnectionState().Subscribe(received.Add);

        conn.RaiseConnectionStateChanged(ConnectionState.Disconnected, ConnectionState.Connecting);
        conn.RaiseConnectionStateChanged(ConnectionState.Connecting, ConnectionState.Connected);

        Assert.Equal(2, received.Count);
        Assert.Equal("plc1", received[0].PlcId);
        Assert.Equal(ConnectionState.Connecting, received[0].State);
        Assert.Equal(ConnectionState.Connected, received[1].State);
    }

    [Fact]
    public void ObserveConnectionState_Dispose_Unsubscribes()
    {
        var conn = new FakeManagedConnection("plc1");
        var count = 0;

        var sub = conn.ObserveConnectionState().Subscribe(_ => count++);
        conn.RaiseConnectionStateChanged(ConnectionState.Disconnected, ConnectionState.Connecting);
        sub.Dispose();
        conn.RaiseConnectionStateChanged(ConnectionState.Connecting, ConnectionState.Connected);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PoolObserveValue_ResolvesTargetAndStreams()
    {
        using var sim = NewSim(); // "plc1"
        var pool = new FakeConnectionPool(
            new Dictionary<string, IAdsConnection>(StringComparer.OrdinalIgnoreCase)
            {
                ["plc1"] = sim,
            });

        var received = new List<AdsValueChange<int>>();
        using var sub = pool.ObserveValue<int>("plc1", "GVL.N", cycleTimeMs: 100)
            .Subscribe(received.Add);

        await sim.WriteValueAsync("GVL.N", 5, CancellationToken.None);

        var change = Assert.Single(received);
        Assert.Equal(new AdsValueChange<int>("GVL.N", 5), change);
    }

    [Fact]
    public void PoolObserveValue_UnknownTarget_SurfacesOnError()
    {
        var pool = new FakeConnectionPool(
            new Dictionary<string, IAdsConnection>(StringComparer.OrdinalIgnoreCase));

        Exception? error = null;
        using var sub = pool.ObserveValue<int>("nope", "GVL.N")
            .Subscribe(_ => { }, ex => error = ex);

        Assert.IsType<UnknownPlcTargetException>(error);
    }

    [Fact]
    public void ObserveAllConnectionStates_MergesEveryTarget()
    {
        var plc1 = new FakeManagedConnection("plc1");
        var plc2 = new FakeManagedConnection("plc2");
        var pool = new FakeConnectionPool(
            new Dictionary<string, IAdsConnection>(StringComparer.OrdinalIgnoreCase)
            {
                ["plc1"] = plc1,
                ["plc2"] = plc2,
            });

        var received = new List<ConnectionStateChangedEventArgs>();
        using var sub = pool.ObserveAllConnectionStates().Subscribe(received.Add);

        plc1.RaiseConnectionStateChanged(ConnectionState.Disconnected, ConnectionState.Connecting);
        plc2.RaiseConnectionStateChanged(ConnectionState.Disconnected, ConnectionState.Connected);

        Assert.Equal(2, received.Count);
        Assert.Contains(received, e => e.PlcId == "plc1" && e.State == ConnectionState.Connecting);
        Assert.Contains(received, e => e.PlcId == "plc2" && e.State == ConnectionState.Connected);
    }
}
