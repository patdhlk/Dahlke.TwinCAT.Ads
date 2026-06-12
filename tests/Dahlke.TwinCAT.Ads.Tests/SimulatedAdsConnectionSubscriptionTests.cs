using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Tests for C16: simulated subscriptions that actually fire on changed writes.
/// Covers on-change semantics, multi-subscriber, dispose, batch writes, exception
/// safety, and facade end-to-end integration.
/// </summary>
public class SimulatedAdsConnectionSubscriptionTests
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(10);

    private static SimulatedAdsConnection CreateConnection()
        => new("test-plc", "Test PLC", NullLoggerFactory.Instance);

    // =========================================================================
    // Basic subscribe-then-write fires callback.
    // =========================================================================

    [Fact]
    public async Task Subscribe_ThenWrite_FiresCallback()
    {
        using var conn = CreateConnection();

        string? receivedPath = null;
        object? receivedValue = null;
        using var sub = await conn.SubscribeAsync("A.x", 100, (p, v) => { receivedPath = p; receivedValue = v; }, CancellationToken.None);

        await conn.WriteValueAsync("A.x", 42, CancellationToken.None);

        Assert.Equal("A.x", receivedPath);
        Assert.Equal(42, receivedValue);
    }

    // =========================================================================
    // On-change semantics: writing the same value a second time → no second call.
    // =========================================================================

    [Fact]
    public async Task Write_SameValueTwice_OnlyOneCallback()
    {
        using var conn = CreateConnection();

        var callCount = 0;
        using var sub = await conn.SubscribeAsync("A.x", 100, (_, _) => callCount++, CancellationToken.None);

        await conn.WriteValueAsync("A.x", 99, CancellationToken.None); // change: null → 99
        await conn.WriteValueAsync("A.x", 99, CancellationToken.None); // no change: 99 → 99

        Assert.Equal(1, callCount);
    }

    // =========================================================================
    // Writing different values fires a callback per change.
    // =========================================================================

    [Fact]
    public async Task Write_DifferentValues_CallbackPerChange()
    {
        using var conn = CreateConnection();

        var received = new List<object?>();
        using var sub = await conn.SubscribeAsync("A.x", 100, (_, v) => received.Add(v), CancellationToken.None);

        await conn.WriteValueAsync("A.x", 1, CancellationToken.None);
        await conn.WriteValueAsync("A.x", 2, CancellationToken.None);
        await conn.WriteValueAsync("A.x", 3, CancellationToken.None);

        Assert.Equal([1, 2, 3], received.Cast<int>().ToArray());
    }

    // =========================================================================
    // Two subscribers on the same path: both fire; disposing one stops only it.
    // =========================================================================

    [Fact]
    public async Task TwoSubscribers_SamePath_BothFire()
    {
        using var conn = CreateConnection();

        var fired1 = 0;
        var fired2 = 0;
        using var sub1 = await conn.SubscribeAsync("A.x", 100, (_, _) => fired1++, CancellationToken.None);
        using var sub2 = await conn.SubscribeAsync("A.x", 100, (_, _) => fired2++, CancellationToken.None);

        await conn.WriteValueAsync("A.x", 10, CancellationToken.None);

        Assert.Equal(1, fired1);
        Assert.Equal(1, fired2);
    }

    [Fact]
    public async Task TwoSubscribers_DisposingOne_StopsOnlyIt()
    {
        using var conn = CreateConnection();

        var fired1 = 0;
        var fired2 = 0;
        var sub1 = await conn.SubscribeAsync("A.x", 100, (_, _) => fired1++, CancellationToken.None);
        using var sub2 = await conn.SubscribeAsync("A.x", 100, (_, _) => fired2++, CancellationToken.None);

        // Write once while both subscribed.
        await conn.WriteValueAsync("A.x", 10, CancellationToken.None);
        Assert.Equal(1, fired1);
        Assert.Equal(1, fired2);

        // Dispose first subscription.
        sub1.Dispose();

        // Write a different value so on-change triggers.
        await conn.WriteValueAsync("A.x", 20, CancellationToken.None);

        Assert.Equal(1, fired1); // stopped
        Assert.Equal(2, fired2); // still alive
    }

    // =========================================================================
    // Dispose stops further callbacks; dispose is idempotent.
    // =========================================================================

    [Fact]
    public async Task Dispose_StopsFurtherCallbacks()
    {
        using var conn = CreateConnection();

        var callCount = 0;
        var sub = await conn.SubscribeAsync("A.x", 100, (_, _) => callCount++, CancellationToken.None);

        await conn.WriteValueAsync("A.x", 1, CancellationToken.None);
        Assert.Equal(1, callCount);

        sub.Dispose();
        // Need a different value for on-change to trigger.
        await conn.WriteValueAsync("A.x", 2, CancellationToken.None);

        Assert.Equal(1, callCount); // no additional call after dispose
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        using var conn = CreateConnection();

        var sub = await conn.SubscribeAsync("A.x", 100, (_, _) => { }, CancellationToken.None);

        sub.Dispose();
        sub.Dispose(); // must not throw
        sub.Dispose();
    }

    // =========================================================================
    // Writing to an unsubscribed path fires nothing.
    // =========================================================================

    [Fact]
    public async Task Write_UnsubscribedPath_NoCallback()
    {
        using var conn = CreateConnection();

        var callCount = 0;
        using var sub = await conn.SubscribeAsync("A.x", 100, (_, _) => callCount++, CancellationToken.None);

        await conn.WriteValueAsync("B.y", 77, CancellationToken.None); // different path

        Assert.Equal(0, callCount);
    }

    // =========================================================================
    // Batch WriteValuesAsync fires per changed symbol.
    // =========================================================================

    [Fact]
    public async Task WriteValuesAsync_FiresPerChangedSymbol()
    {
        using var conn = CreateConnection();

        var receivedByPath = new Dictionary<string, List<object?>>();
        using var subA = await conn.SubscribeAsync("A.x", 100, (p, v) =>
        {
            if (!receivedByPath.ContainsKey(p)) receivedByPath[p] = [];
            receivedByPath[p].Add(v);
        }, CancellationToken.None);
        using var subB = await conn.SubscribeAsync("B.y", 100, (p, v) =>
        {
            if (!receivedByPath.ContainsKey(p)) receivedByPath[p] = [];
            receivedByPath[p].Add(v);
        }, CancellationToken.None);

        // First batch: both paths change.
        await conn.WriteValuesAsync(new Dictionary<string, object> { ["A.x"] = 1, ["B.y"] = 2 }, CancellationToken.None);
        Assert.Equal([1], receivedByPath["A.x"].Cast<int>().ToArray());
        Assert.Equal([2], receivedByPath["B.y"].Cast<int>().ToArray());

        // Second batch: only A.x changes (B.y same value → no callback).
        await conn.WriteValuesAsync(new Dictionary<string, object> { ["A.x"] = 3, ["B.y"] = 2 }, CancellationToken.None);
        Assert.Equal([1, 3], receivedByPath["A.x"].Cast<int>().ToArray());
        Assert.Equal([2], receivedByPath["B.y"].Cast<int>().ToArray()); // still just the one
    }

    // =========================================================================
    // Callback throwing doesn't fail the write or other callbacks.
    // =========================================================================

    [Fact]
    public async Task CallbackThrowing_DoesNotFailWrite_OrOtherCallbacks()
    {
        using var conn = CreateConnection();

        var goodFired = 0;
        using var badSub = await conn.SubscribeAsync("A.x", 100, (_, _) => throw new InvalidOperationException("boom"), CancellationToken.None);
        using var goodSub = await conn.SubscribeAsync("A.x", 100, (_, _) => goodFired++, CancellationToken.None);

        // The write must complete without throwing.
        var ex = await Record.ExceptionAsync(() => conn.WriteValueAsync("A.x", 42, CancellationToken.None));
        Assert.Null(ex);

        // The good callback still ran.
        Assert.Equal(1, goodFired);
    }

    // =========================================================================
    // SetInitialValues does NOT fire callbacks (seeding before subscribers).
    // =========================================================================

    [Fact]
    public async Task SetInitialValues_DoesNotFireCallbacks()
    {
        using var conn = CreateConnection();

        var callCount = 0;
        using var sub = await conn.SubscribeAsync("A.x", 100, (_, _) => callCount++, CancellationToken.None);

        // Seeding via SetInitialValues after subscription: still no callback.
        conn.SetInitialValues(new Dictionary<string, object?> { ["A.x"] = 999 });

        Assert.Equal(0, callCount);
    }

    // =========================================================================
    // Boxed-type Equals semantics: int 42 vs double 42.0 count as a change.
    // =========================================================================

    [Fact]
    public async Task BoxedTypeChange_IntVsDouble_CountsAsChange()
    {
        using var conn = CreateConnection();

        var callCount = 0;
        using var sub = await conn.SubscribeAsync("A.x", 100, (_, _) => callCount++, CancellationToken.None);

        await conn.WriteValueAsync("A.x", 42,    CancellationToken.None); // int box
        await conn.WriteValueAsync("A.x", 42.0,  CancellationToken.None); // double box — different type → change

        Assert.Equal(2, callCount);
    }

    // =========================================================================
    // cycleTimeMs is accepted but simulation delivers immediately on change.
    // =========================================================================

    [Fact]
    public async Task SubscribeAsync_AcceptsCycleTimeMs_ReturnsDisposable()
    {
        using var conn = CreateConnection();

        // Verify various cycleTimeMs values are accepted without throwing.
        using var sub1 = await conn.SubscribeAsync("A.x", 0, (_, _) => { }, CancellationToken.None);
        using var sub2 = await conn.SubscribeAsync("A.x", 1000, (_, _) => { }, CancellationToken.None);
        using var sub3 = await conn.SubscribeAsync("A.x", int.MaxValue, (_, _) => { }, CancellationToken.None);
    }

    // =========================================================================
    // Facade end-to-end: subscribe via pool facade → write via facade → callback fires.
    // =========================================================================

    [Fact]
    public async Task FacadeEndToEnd_SubscribeAndWrite_CallbackFires()
    {
        var factory = new AdsConnectionFactory(NullLoggerFactory.Instance);
        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal();

        var adsOptions = new TwinCatAdsOptions
        {
            Targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["sim1"] = new PlcTargetOptions
                {
                    Mode = ConnectionMode.Simulated,
                    DisplayName = "Sim",
                    InitialValues = new() { ["MAIN.nSpeed"] = 0 },
                },
            },
        };

        var pool = new AdsConnectionPool(
            Options.Create(adsOptions), factory, signal,
            NullLogger<AdsConnectionPool>.Instance, time);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        // Wait for the facade to report connected (underlying sim connection published).
        var deadline = DateTime.UtcNow + RealTimeout;
        while (pool.GetConnection("sim1") is not { IsConnected: true })
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Facade never became connected.");
            await Task.Delay(5);
        }

        var connection = pool.GetConnection("sim1")!;

        object? receivedValue = null;
        string? receivedPath = null;
        using var handle = await connection.SubscribeAsync(
            "MAIN.nSpeed", 100,
            (p, v) => { receivedPath = p; receivedValue = v; },
            CancellationToken.None).WaitAsync(RealTimeout);

        // Write a changed value through the facade.
        await connection.WriteValueAsync("MAIN.nSpeed", 1500, CancellationToken.None);

        Assert.Equal("MAIN.nSpeed", receivedPath);
        Assert.Equal(1500, receivedValue);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }
}
