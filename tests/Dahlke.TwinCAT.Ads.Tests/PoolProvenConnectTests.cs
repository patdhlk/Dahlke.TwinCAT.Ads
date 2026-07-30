using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Pins the prove-before-publish contract (issue #12): a Beckhoff
/// <c>Connect()</c> is purely local (it associates an AMS address without a
/// round trip), so the pool must prove the link with one real round trip
/// (<c>IsAliveAsync</c>) BEFORE publishing the connection — before
/// <c>_connections</c> is written, before the facade's <c>SetCurrent</c> (and
/// therefore before subscription re-registration), and before
/// <c>ConnectionState.Connected</c> is raised. A failed probe is a failed
/// connect attempt: tear down unpublished, back off, retry.
///
/// Mirrors the FakeTimeProvider/FakeManagedConnection timing model of
/// <see cref="AdsConnectionPoolStateTests"/>. State raise points along one
/// reconnect iteration are now:
///   Disconnected (initial)
///   -&gt; Connecting   (before ads.Connect())
///   -&gt; Connected    (after Connect() AND a successful liveness probe)
///   -&gt; Disconnected (probe failure / health-check failure / connect exception)
/// </summary>
public class PoolProvenConnectTests
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Health = TimeSpan.FromSeconds(5);

    private static (AdsConnectionPool pool, FakeConnectionFactory factory, FakeTimeProvider time, AdsRouterReadySignal signal)
        CreatePool(params string[] plcIds)
    {
        if (plcIds.Length == 0) plcIds = ["plc1"];

        var targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in plcIds)
            targets[id] = new PlcTargetOptions { DisplayName = id, AmsNetId = "1.2.3.4.5.6" };

        var adsOptions = new TwinCatAdsOptions { Targets = targets };

        var factory = new FakeConnectionFactory();
        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal();

        var pool = new AdsConnectionPool(
            Options.Create(adsOptions),
            factory,
            signal,
            NullLoggerFactory.Instance,
            time);

        return (pool, factory, time, signal);
    }

    private sealed class StateRecorder
    {
        private readonly object _gate = new();
        private readonly List<(string PlcId, ConnectionState State)> _events = new();

        public void Attach(AdsConnectionPool pool)
            => pool.ConnectionStateChanged += (_, e) =>
            {
                lock (_gate) { _events.Add((e.PlcId, e.State)); }
            };

        public List<ConnectionState> StatesFor(string plcId)
        {
            lock (_gate)
            {
                return _events
                    .Where(e => string.Equals(e.PlcId, plcId, StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.State)
                    .ToList();
            }
        }

        public int CountOf(string plcId, ConnectionState state)
            => StatesFor(plcId).Count(s => s == state);
    }

    private static async Task AdvanceUntil(FakeTimeProvider time, Func<bool> predicate, TimeSpan step)
    {
        var deadline = DateTime.UtcNow + RealTimeout;
        while (!predicate())
        {
            time.Advance(step);
            await Task.Delay(TimeSpan.FromMilliseconds(10));
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Predicate did not become true within the real-time guard window.");
        }
    }

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

    // =====================================================================

    [Fact]
    public async Task ProbeFailureOnConnect_NeverPublishesConnected_UntilAProvenAttempt()
    {
        var (pool, factory, time, signal) = CreatePool("plc1");
        var rec = new StateRecorder();
        rec.Attach(pool);

        // Attempt 1: Connect() succeeds (it is purely local), but the link
        // cannot carry traffic yet — the probe must fail the attempt.
        var unproven = new FakeManagedConnection("plc1");
        unproven.IsAliveResults.Enqueue(false);
        factory.Enqueue(unproven);

        // Attempt 2: the peer has re-established its half of the route.
        var proven = new FakeManagedConnection("plc1");
        factory.Enqueue(proven);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await unproven.ConnectCalled.WaitAsync(RealTimeout);

        // Drive teardown grace + backoff until the second attempt is proven.
        await AdvanceUntil(
            time,
            () => rec.CountOf("plc1", ConnectionState.Connected) >= 1,
            TimeSpan.FromSeconds(2));

        // Exactly one Connected, and it belongs to the proven attempt:
        // the unproven connection cycles Connecting -> Disconnected.
        Assert.Equal(
            [
                ConnectionState.Connecting,
                ConnectionState.Disconnected,
                ConnectionState.Connecting,
                ConnectionState.Connected,
            ],
            rec.StatesFor("plc1"));

        // The probe ran against the unproven connection before any publish,
        // and the unproven connection was torn down like a failed connect.
        Assert.True(unproven.IsAliveCount >= 1);
        Assert.Equal(1, unproven.DisposeCount);

        Assert.Equal(ConnectionState.Connected, pool.GetState("plc1"));
        Assert.True(pool.GetConnection("plc1").IsConnected);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task ProbeAlwaysFailing_ReportsUnavailable_NeverConnected()
    {
        var (pool, factory, time, signal) = CreatePool("plc1");
        var rec = new StateRecorder();
        rec.Attach(pool);

        // The misconfigured-AmsNetId symptom from #12: every attempt's local
        // Connect() succeeds, no attempt can carry traffic.
        for (int i = 0; i < 8; i++)
            factory.Enqueue(new FakeManagedConnection("plc1") { IsAliveDefault = false });

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await AdvanceUntil(
            time,
            () => rec.CountOf("plc1", ConnectionState.Connecting) >= 2
                  && rec.CountOf("plc1", ConnectionState.Disconnected) >= 2,
            TimeSpan.FromSeconds(2));

        // Never Connected — and the consumer-visible surface agrees: the
        // facade routes nowhere, so IsConnected is false rather than the
        // "green while nothing works" of #12.
        Assert.DoesNotContain(ConnectionState.Connected, rec.StatesFor("plc1"));
        Assert.NotEqual(ConnectionState.Connected, pool.GetState("plc1"));
        Assert.False(pool.GetConnection("plc1").IsConnected);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task Reregistration_TargetsOnlyProvenConnections()
    {
        var (pool, factory, time, signal) = CreatePool("plc1");
        var rec = new StateRecorder();
        rec.Attach(pool);

        // Iteration 1: proven at connect, then a health check fails -> rebuild.
        var first = new FakeManagedConnection("plc1");
        first.IsAliveResults.Enqueue(true);  // connect-time probe
        first.IsAliveResults.Enqueue(false); // first health check -> reconnect
        factory.Enqueue(first);

        // Iteration 2: the #12 window — Connect() succeeds locally, link dead.
        // Re-registration must NOT be attempted here.
        var unproven = new FakeManagedConnection("plc1");
        unproven.IsAliveResults.Enqueue(false);
        factory.Enqueue(unproven);

        // Iteration 3: link is genuinely back.
        var recovered = new FakeManagedConnection("plc1");
        factory.Enqueue(recovered);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await first.ConnectCalled.WaitAsync(RealTimeout);
        await WaitUntil(() => rec.CountOf("plc1", ConnectionState.Connected) >= 1);

        // Durable subscription registered against the first (proven) connection.
        var connection = pool.GetConnection("plc1");
        using var subscription = await connection.SubscribeAsync(
            "MAIN.counter", 100, (_, _) => { }, CancellationToken.None);
        Assert.Single(first.Subscriptions);

        // Kill iteration 1 via its scripted health-check failure, then drive
        // the loop through the unproven attempt into the recovered one.
        await AdvanceUntil(
            time,
            () => rec.CountOf("plc1", ConnectionState.Connected) >= 2,
            Health);

        // The durable subscription came back on the recovered connection...
        await WaitUntil(() => recovered.Subscriptions.Count == 1);

        // ...and was never attempted against the unproven one: exactly one
        // "subscribed" per outage, on a link that could actually carry it.
        Assert.Empty(unproven.Subscriptions);

        // Exactly two Connected publishes total (initial + recovery) — not one
        // per unproven attempt as in the observed #12 log.
        Assert.Equal(2, rec.CountOf("plc1", ConnectionState.Connected));

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }
}
