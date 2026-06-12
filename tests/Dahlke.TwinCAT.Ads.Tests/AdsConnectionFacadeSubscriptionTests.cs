using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TwinCAT.Ads;
using Rec = Dahlke.TwinCAT.Ads.Tests.Fakes.FakeManagedConnection.SubscriptionRecord;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Tests for durable subscriptions (C15): a subscription made through the
/// <see cref="AdsConnectionFacade"/> survives reconnects. The facade holds the
/// subscription record and re-registers it against each newly published
/// connection; the caller's <see cref="IDisposable"/> stays valid across
/// reconnects, and disposing it removes the subscription permanently.
/// </summary>
public class AdsConnectionFacadeSubscriptionTests
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(15);

    private static AdsConnectionFacade NewFacade(FakeTimeProvider time, int timeoutMs = 5000)
        => new("plc1", new PlcTargetOptions { DisplayName = "PLC One", TimeoutMs = timeoutMs }, time, NullLogger.Instance);

    private static async Task WaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + RealTimeout;
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Predicate did not become true within the real-time guard window.");
            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }
    }

    private static Rec Only(FakeManagedConnection conn) => Assert.Single(conn.Subscriptions);

    // =====================================================================
    // Subscribe while connected.
    // =====================================================================

    [Fact]
    public async Task Subscribe_WhileConnected_RegistersOnCurrent_CallbackReceivesNotification()
    {
        var time = new FakeTimeProvider();
        var facade = NewFacade(time);
        var conn = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(conn);

        object? received = null;
        string? receivedPath = null;
        var handle = await facade.SubscribeAsync("MAIN.x", 100, (p, v) => { receivedPath = p; received = v; }, CancellationToken.None)
            .WaitAsync(RealTimeout);

        var rec = Only(conn);
        Assert.Equal("MAIN.x", rec.Path);
        Assert.Equal(100, rec.CycleTimeMs);

        rec.FireNotification(42);
        Assert.Equal("MAIN.x", receivedPath);
        Assert.Equal(42, received);

        handle.Dispose();
    }

    // =====================================================================
    // Re-registration on reconnect.
    // =====================================================================

    [Fact]
    public async Task Reconnect_ReRegistersSubscription_OnNewConnection_OldHandleStillValid()
    {
        var time = new FakeTimeProvider();
        var facade = NewFacade(time);
        var first = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(first);

        var fired = 0;
        var handle = await facade.SubscribeAsync("MAIN.x", 100, (_, _) => Interlocked.Increment(ref fired), CancellationToken.None)
            .WaitAsync(RealTimeout);
        Assert.Single(first.Subscriptions);

        // New connection published (reconnect). The facade re-registers in the
        // background; the handle is unchanged.
        var second = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(second);

        await WaitUntil(() => second.Subscriptions.Count == 1);
        var rec = Only(second);
        Assert.Equal("MAIN.x", rec.Path);
        Assert.Equal(100, rec.CycleTimeMs);

        // The callback fires from the NEW connection.
        rec.FireNotification(7);
        Assert.Equal(1, fired);

        handle.Dispose();
    }

    [Fact]
    public async Task Reconnect_ReRegistersAllSubscriptions()
    {
        var time = new FakeTimeProvider();
        var facade = NewFacade(time);
        var first = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(first);

        var h1 = await facade.SubscribeAsync("A", 10, (_, _) => { }, CancellationToken.None).WaitAsync(RealTimeout);
        var h2 = await facade.SubscribeAsync("B", 20, (_, _) => { }, CancellationToken.None).WaitAsync(RealTimeout);
        var h3 = await facade.SubscribeAsync("C", 30, (_, _) => { }, CancellationToken.None).WaitAsync(RealTimeout);
        Assert.Equal(3, first.Subscriptions.Count);

        var second = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(second);

        await WaitUntil(() => second.Subscriptions.Count == 3);
        var paths = second.Subscriptions.Select(s => s.Path).OrderBy(p => p).ToArray();
        Assert.Equal(["A", "B", "C"], paths);

        h1.Dispose();
        h2.Dispose();
        h3.Dispose();
    }

    // =====================================================================
    // Dispose handle.
    // =====================================================================

    [Fact]
    public async Task Dispose_RemovesRecord_DisposesUnderlying_NoReRegistrationAfterSubsequentReconnect()
    {
        var time = new FakeTimeProvider();
        var facade = NewFacade(time);
        var first = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(first);

        var handle = await facade.SubscribeAsync("MAIN.x", 100, (_, _) => { }, CancellationToken.None).WaitAsync(RealTimeout);
        var rec = Only(first);
        Assert.False(rec.IsDisposed);

        handle.Dispose();

        // The underlying registration on the live connection was disposed.
        Assert.True(rec.IsDisposed);

        // A subsequent reconnect gets NO registration: the record is gone.
        var second = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(second);

        // Give any (erroneous) re-registration a chance to land, then assert none did.
        await Task.Delay(50);
        Assert.Empty(second.Subscriptions);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var time = new FakeTimeProvider();
        var facade = NewFacade(time);
        var conn = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(conn);

        var handle = await facade.SubscribeAsync("MAIN.x", 100, (_, _) => { }, CancellationToken.None).WaitAsync(RealTimeout);
        var rec = Only(conn);

        handle.Dispose();
        handle.Dispose();
        handle.Dispose();

        Assert.True(rec.IsDisposed);
    }

    // =====================================================================
    // Subscribe during outage.
    // =====================================================================

    [Fact]
    public async Task Subscribe_DuringOutage_Parks_ThenLandsOnConnectionPublishedMidWait()
    {
        var time = new FakeTimeProvider();
        var facade = NewFacade(time);

        // No current connection: the subscribe parks like any operation.
        var subTask = facade.SubscribeAsync("MAIN.x", 100, (_, _) => { }, CancellationToken.None);
        Assert.False(subTask.IsCompleted);

        var conn = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(conn);

        var handle = await subTask.WaitAsync(RealTimeout);
        await WaitUntil(() => conn.Subscriptions.Count == 1);
        Assert.Equal("MAIN.x", Only(conn).Path);

        handle.Dispose();
    }

    // =====================================================================
    // Re-registration failure -> logged, record retained, retried next reconnect.
    // =====================================================================

    [Fact]
    public async Task ReRegistration_Failure_RetainsRecord_RetriesOnNextReconnect()
    {
        var time = new FakeTimeProvider();
        var facade = NewFacade(time);
        var first = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(first);

        var handle = await facade.SubscribeAsync("MAIN.x", 100, (_, _) => { }, CancellationToken.None).WaitAsync(RealTimeout);
        Assert.Single(first.Subscriptions);

        // Reconnect #1: the new connection's SubscribeAsync throws once.
        var second = new FakeManagedConnection("plc1") { IsConnected = true };
        second.SubscribeThrowsOnce = new InvalidOperationException("transient subscribe failure");
        facade.SetCurrent(second);

        // The failed re-registration is observed (SubscribeAsync was entered) but
        // produced no recorded subscription.
        await second.SubscribeCalled.WaitAsync(RealTimeout);
        await Task.Delay(50);
        Assert.Empty(second.Subscriptions);

        // Reconnect #2: the record was retained, so it re-registers successfully.
        var third = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(third);

        await WaitUntil(() => third.Subscriptions.Count == 1);
        Assert.Equal("MAIN.x", Only(third).Path);

        handle.Dispose();
    }

    // =====================================================================
    // Dispose racing an in-flight re-registration -> no leaked registration.
    // =====================================================================

    [Fact]
    public async Task Dispose_RacingInFlightReRegistration_NoLeakedRegistration()
    {
        var time = new FakeTimeProvider();
        var facade = NewFacade(time);
        var first = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(first);

        var handle = await facade.SubscribeAsync("MAIN.x", 100, (_, _) => { }, CancellationToken.None).WaitAsync(RealTimeout);

        // The new connection holds its SubscribeAsync open on a gate the test controls.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new FakeManagedConnection("plc1") { IsConnected = true, SubscribeGate = gate.Task };
        facade.SetCurrent(second);

        // Wait until the re-registration is in flight (entered SubscribeAsync, blocked on gate).
        await second.SubscribeCalled.WaitAsync(RealTimeout);

        // Dispose the handle WHILE the re-registration is parked on the gate.
        handle.Dispose();

        // Release the gate: the in-flight re-registration completes and produces a
        // registration that the facade must dispose (record already removed).
        gate.SetResult();

        // The registration lands in the fake's list, but must be disposed by the facade.
        await WaitUntil(() => second.Subscriptions.Count == 1);
        var rec = Only(second);
        await WaitUntil(() => rec.IsDisposed);
        Assert.True(rec.IsDisposed);
    }

    [Fact]
    public async Task ReRegistration_AgainstStaleConnection_DisposesWhatItCreated()
    {
        var time = new FakeTimeProvider();
        var facade = NewFacade(time);
        var first = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(first);

        var handle = await facade.SubscribeAsync("MAIN.x", 100, (_, _) => { }, CancellationToken.None).WaitAsync(RealTimeout);

        // Second connection's re-registration is gated open.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new FakeManagedConnection("plc1") { IsConnected = true, SubscribeGate = gate.Task };
        facade.SetCurrent(second);
        await second.SubscribeCalled.WaitAsync(RealTimeout);

        // A THIRD connection arrives and wins before second's re-registration finishes.
        var third = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(third);
        await WaitUntil(() => third.Subscriptions.Count == 1); // third re-registers fine

        // Now let second's in-flight re-registration finish. It is stale (third is
        // current), so what it created must be disposed, not stored as the record's
        // live registration.
        gate.SetResult();
        await WaitUntil(() => second.Subscriptions.Count == 1);
        var staleRec = Only(second);
        await WaitUntil(() => staleRec.IsDisposed);

        // The live registration is still third's, and it is NOT disposed.
        Assert.False(Only(third).IsDisposed);

        handle.Dispose();
        // Disposing the handle disposes third's live registration.
        Assert.True(Only(third).IsDisposed);
    }

    // =====================================================================
    // Stopped facade.
    // =====================================================================

    [Fact]
    public async Task Subscribe_AfterMarkStopped_FailsFast()
    {
        var time = new FakeTimeProvider();
        var facade = NewFacade(time, timeoutMs: 60_000);
        facade.MarkStopped();

        await Assert.ThrowsAsync<AdsConnectionUnavailableException>(
            () => facade.SubscribeAsync("X", 100, (_, _) => { }, CancellationToken.None).WaitAsync(RealTimeout));
    }

    [Fact]
    public async Task MarkStopped_NoReRegistration_ActiveHandleDisposeStillSafe()
    {
        var time = new FakeTimeProvider();
        var facade = NewFacade(time);
        var conn = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(conn);

        var handle = await facade.SubscribeAsync("MAIN.x", 100, (_, _) => { }, CancellationToken.None).WaitAsync(RealTimeout);

        facade.MarkStopped();

        // A SetCurrent after stop must not re-register (the stopped facade rolls
        // the current pointer back). No new registration appears anywhere.
        var revived = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(revived);
        await Task.Delay(50);
        Assert.Empty(revived.Subscriptions);

        // Disposing the handle after stop is still safe (idempotent, no throw).
        handle.Dispose();
        handle.Dispose();
    }

    // =====================================================================
    // Pool-level integration: full subscribe -> outage -> recovery cycle.
    // =====================================================================

    [Fact]
    public async Task PoolIntegration_Subscription_SurvivesHealthCheckReconnect()
    {
        var first = new FakeManagedConnection("plc1");
        first.IsAliveResults.Enqueue(false); // health check fails -> rebuild
        var second = new FakeManagedConnection("plc1");
        var factory = new FakeConnectionFactory();
        factory.Enqueue(first);
        factory.Enqueue(second);

        var adsOptions = new TwinCatAdsOptions
        {
            Targets = new(StringComparer.OrdinalIgnoreCase)
            {
                ["plc1"] = new PlcTargetOptions { DisplayName = "plc1", AmsNetId = "1.2.3.4.5.6", TimeoutMs = 60_000 },
            },
        };
        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal();
        var pool = new AdsConnectionPool(
            Options.Create(adsOptions), factory, signal, NullLoggerFactory.Instance, time);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        var facade = Assert.IsType<AdsConnectionFacade>(pool.GetConnection("plc1"));
        await WaitUntil(() => ReferenceEquals(facade.CurrentForTesting, first));

        var fired = 0;
        var handle = await facade.SubscribeAsync("MAIN.x", 100, (_, _) => Interlocked.Increment(ref fired), CancellationToken.None)
            .WaitAsync(RealTimeout);
        await WaitUntil(() => first.Subscriptions.Count == 1);
        Only(first).FireNotification(1);
        Assert.Equal(1, fired);

        // Drive the health-check failure -> reconnect onto `second`.
        var deadline = DateTime.UtcNow + RealTimeout;
        while (!ReferenceEquals(facade.CurrentForTesting, second))
        {
            time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(10));
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Pool never reconnected onto the second connection.");
        }

        // The subscription re-registered on the new connection; the callback works again.
        await WaitUntil(() => second.Subscriptions.Count == 1);
        Only(second).FireNotification(2);
        Assert.Equal(2, fired);

        handle.Dispose();
        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }
}
