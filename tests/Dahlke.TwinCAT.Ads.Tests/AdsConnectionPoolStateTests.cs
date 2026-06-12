using System.Collections.Concurrent;
using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Tests for the connection-state model layered onto
/// <see cref="AdsConnectionPool"/>: the <c>ConnectionStateChanged</c> event, the
/// <c>SetState</c> transition plumbing (raise-only-on-change, handler-exception
/// isolation), and <c>GetState</c>.
///
/// These mirror the timing model and FakeTimeProvider/FakeManagedConnection
/// patterns of <see cref="AdsConnectionPoolTests"/>. State raise points along
/// one reconnect iteration:
///   Disconnected (initial)
///   -> Connecting   (before ads.Connect())
///   -> Connected    (after _connections[plcId] = ads)
///   -> Disconnected (health-check failure / connect exception / teardown)
/// A persistent failure therefore cycles Connecting -> Disconnected each attempt.
/// </summary>
public class AdsConnectionPoolStateTests
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
            NullLogger<AdsConnectionPool>.Instance,
            time);

        return (pool, factory, time, signal);
    }

    /// <summary>
    /// Records every state transition raised by the pool in order, and exposes a
    /// re-armable hook that completes once a target's observed states contain a
    /// given subsequence — letting a test await the loop reaching a known point
    /// before advancing fake time.
    /// </summary>
    private sealed class StateRecorder
    {
        private readonly object _gate = new();
        private readonly List<(string PlcId, ConnectionState Prev, ConnectionState State)> _events = new();

        public void Attach(AdsConnectionPool pool)
            => pool.ConnectionStateChanged += (_, e) =>
            {
                lock (_gate) { _events.Add((e.PlcId, e.PreviousState, e.State)); }
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

        public List<(ConnectionState Prev, ConnectionState State)> PairsFor(string plcId)
        {
            lock (_gate)
            {
                return _events
                    .Where(e => string.Equals(e.PlcId, plcId, StringComparison.OrdinalIgnoreCase))
                    .Select(e => (e.Prev, e.State))
                    .ToList();
            }
        }

        public int CountOf(string plcId, ConnectionState state)
            => StatesFor(plcId).Count(s => s == state);
    }

    /// <summary>
    /// Advance fake time in small steps until <paramref name="predicate"/> holds
    /// (or the real-time guard trips). Real time only paces the walk; the
    /// predicate over recorded state is the synchronisation signal.
    /// </summary>
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
    public async Task SuccessfulStart_RaisesConnectingThenConnected()
    {
        var (pool, factory, _, signal) = CreatePool("plc1");
        var rec = new StateRecorder();
        rec.Attach(pool);

        var conn = factory.Enqueue(new FakeManagedConnection("plc1"));

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await conn.ConnectCalled.WaitAsync(RealTimeout);
        await WaitUntil(() => rec.StatesFor("plc1").Contains(ConnectionState.Connected));

        var states = rec.StatesFor("plc1");
        // Disconnected -> Connecting -> Connected. The initial Disconnected is
        // raised on the first transition away from the implicit Disconnected
        // start; assert the observable ordering ends Connecting, Connected.
        Assert.Equal(
            [ConnectionState.Connecting, ConnectionState.Connected],
            states);

        Assert.Equal(ConnectionState.Connected, pool.GetState("plc1"));

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task HealthCheckFailureThenRecovery_RaisesConnectedDisconnectedConnectingConnected()
    {
        var (pool, factory, time, signal) = CreatePool("plc1");
        var rec = new StateRecorder();
        rec.Attach(pool);

        var first = new FakeManagedConnection("plc1");
        first.IsAliveResults.Enqueue(false); // first health check fails -> rebuild
        factory.Enqueue(first);

        var second = new FakeManagedConnection("plc1");
        factory.Enqueue(second);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await first.ConnectCalled.WaitAsync(RealTimeout);
        await WaitUntil(() => rec.StatesFor("plc1").Contains(ConnectionState.Connected));

        // Drive the health interval until IsAliveAsync fires (returns false),
        // then keep advancing until the rebuild reaches Connected again.
        await AdvanceUntil(
            time,
            () => rec.CountOf("plc1", ConnectionState.Connected) >= 2,
            Health);

        await second.ConnectCalled.WaitAsync(RealTimeout);

        var states = rec.StatesFor("plc1");
        // Connecting, Connected, Disconnected, Connecting, Connected.
        Assert.Equal(
            [
                ConnectionState.Connecting,
                ConnectionState.Connected,
                ConnectionState.Disconnected,
                ConnectionState.Connecting,
                ConnectionState.Connected,
            ],
            states);

        Assert.Equal(ConnectionState.Connected, pool.GetState("plc1"));

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task PersistentConnectFailure_CyclesConnectingDisconnectedPerAttempt()
    {
        var (pool, factory, time, signal) = CreatePool("plc1");
        var rec = new StateRecorder();
        rec.Attach(pool);

        for (int i = 0; i < 8; i++)
            factory.Enqueue(new FakeManagedConnection("plc1") { ConnectShouldThrow = true });

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        // Wait for at least two full Connecting->Disconnected cycles.
        await AdvanceUntil(
            time,
            () => rec.CountOf("plc1", ConnectionState.Connecting) >= 2
                  && rec.CountOf("plc1", ConnectionState.Disconnected) >= 2,
            TimeSpan.FromSeconds(2));

        var pairs = rec.PairsFor("plc1");

        // Every Connecting must be immediately followed by Disconnected (no
        // Connected ever interleaves), and the transitions must be the real
        // change pairs Disconnected->Connecting and Connecting->Disconnected.
        Assert.DoesNotContain(ConnectionState.Connected, rec.StatesFor("plc1"));

        for (int i = 0; i + 1 < pairs.Count; i++)
        {
            if (pairs[i].State == ConnectionState.Connecting)
            {
                Assert.Equal(ConnectionState.Disconnected, pairs[i].Prev);
                Assert.Equal(ConnectionState.Disconnected, pairs[i + 1].State);
                Assert.Equal(ConnectionState.Connecting, pairs[i + 1].Prev);
            }
        }

        Assert.True(rec.CountOf("plc1", ConnectionState.Connecting) >= 2);
        Assert.True(rec.CountOf("plc1", ConnectionState.Disconnected) >= 2);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task StopAsync_LeavesStateDisconnected()
    {
        var (pool, factory, _, signal) = CreatePool("plc1");
        var rec = new StateRecorder();
        rec.Attach(pool);

        var conn = factory.Enqueue(new FakeManagedConnection("plc1"));

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await conn.ConnectCalled.WaitAsync(RealTimeout);
        await WaitUntil(() => pool.GetState("plc1") == ConnectionState.Connected);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);

        Assert.Equal(ConnectionState.Disconnected, pool.GetState("plc1"));
        Assert.Equal(ConnectionState.Disconnected, rec.StatesFor("plc1")[^1]);
    }

    [Fact]
    public async Task GetState_ReflectsCurrentState_AndUnknownIdIsDisconnected()
    {
        var (pool, factory, _, signal) = CreatePool("plc1");

        // Unknown before anything starts.
        Assert.Equal(ConnectionState.Disconnected, pool.GetState("plc1"));
        Assert.Equal(ConnectionState.Disconnected, pool.GetState("never-configured"));

        var conn = factory.Enqueue(new FakeManagedConnection("plc1"));

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await conn.ConnectCalled.WaitAsync(RealTimeout);
        await WaitUntil(() => pool.GetState("plc1") == ConnectionState.Connected);

        Assert.Equal(ConnectionState.Connected, pool.GetState("plc1"));
        Assert.Equal(ConnectionState.Disconnected, pool.GetState("never-configured"));

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task HandlerThrowing_DoesNotBreakReconnection()
    {
        var (pool, factory, time, signal) = CreatePool("plc1");

        // A throwing handler on every transition must not kill the loop.
        var raised = new ConcurrentQueue<ConnectionState>();
        pool.ConnectionStateChanged += (_, e) =>
        {
            raised.Enqueue(e.State);
            throw new InvalidOperationException("handler boom");
        };

        // Iteration 1: connect succeeds, first health check fails -> rebuild.
        var first = new FakeManagedConnection("plc1");
        first.IsAliveResults.Enqueue(false);
        factory.Enqueue(first);

        // Iteration 2: connect succeeds and stays healthy.
        var second = new FakeManagedConnection("plc1");
        factory.Enqueue(second);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        // Connect proceeds despite the throwing handler.
        await first.ConnectCalled.WaitAsync(RealTimeout);
        await WaitUntil(() => pool.GetState("plc1") == ConnectionState.Connected);

        // Health failure -> reconnection still proceeds to a second connect and
        // subsequent events are still raised (proving the loop survived).
        await AdvanceUntil(
            time,
            () => raised.Count(s => s == ConnectionState.Connected) >= 2,
            Health);

        await second.ConnectCalled.WaitAsync(RealTimeout);
        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(ConnectionState.Connected, pool.GetState("plc1"));

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task ForceReconnect_OnConnectedTarget_EmitsDisconnectedBeforeConnecting()
    {
        var (pool, factory, _, signal) = CreatePool("plc1");
        var rec = new StateRecorder();
        rec.Attach(pool);

        var first = factory.Enqueue(new FakeManagedConnection("plc1"));
        var second = factory.Enqueue(new FakeManagedConnection("plc1"));

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await first.ConnectCalled.WaitAsync(RealTimeout);
        await WaitUntil(() => rec.StatesFor("plc1").Contains(ConnectionState.Connected));

        // Force reconnect while Connected
        pool.ForceReconnect("plc1");

        await second.ConnectCalled.WaitAsync(RealTimeout);
        await WaitUntil(() => rec.CountOf("plc1", ConnectionState.Connected) >= 2);

        var states = rec.StatesFor("plc1");
        // Must contain Disconnected between the two Connected states
        var firstConnectedIdx = states.IndexOf(ConnectionState.Connected);
        var disconnectedAfter = states.Skip(firstConnectedIdx + 1).ToList();
        Assert.Contains(ConnectionState.Disconnected, disconnectedAfter);
        Assert.Contains(ConnectionState.Connecting, disconnectedAfter);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task ForceReconnect_DifferentCasing_ReplacesLoopNotDuplicate()
    {
        var (pool, factory, _, signal) = CreatePool("plc1");

        var first = factory.Enqueue(new FakeManagedConnection("plc1"));
        var second = factory.Enqueue(new FakeManagedConnection("plc1"));

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await first.ConnectCalled.WaitAsync(RealTimeout);
        await WaitUntil(() => pool.GetState("plc1") == ConnectionState.Connected);

        var createsBefore = Volatile.Read(ref factory.CreateCount);

        // ForceReconnect with upper-case ID — must resolve to same target
        pool.ForceReconnect("PLC1");

        await second.ConnectCalled.WaitAsync(RealTimeout);
        await WaitUntil(() => pool.GetState("plc1") == ConnectionState.Connected);

        // Exactly one new Create — not two (which would indicate a duplicate loop)
        Assert.Equal(createsBefore + 1, Volatile.Read(ref factory.CreateCount));

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }
}
