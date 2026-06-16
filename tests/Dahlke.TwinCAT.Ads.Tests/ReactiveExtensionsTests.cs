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
}
