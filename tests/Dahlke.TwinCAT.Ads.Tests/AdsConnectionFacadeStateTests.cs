using System.Collections.Concurrent;
using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Tests for C14: the connection-state surface exposed on
/// <see cref="IAdsConnection"/> via the facade — the <c>State</c> property and the
/// <c>ConnectionStateChanged</c> event. The pool already owns the authoritative
/// state machine and an internal <c>ConnectionStateChanged</c> event keyed by
/// plcId; C14 forwards each target's transitions onto the matching
/// <see cref="AdsConnectionFacade"/> so a caller holding the stable
/// <see cref="IAdsConnection"/> can observe and react to connectivity changes
/// without ever touching the pool.
///
/// These mirror the timing model of <see cref="AdsConnectionPoolStateTests"/>
/// (FakeTimeProvider, FakeManagedConnection, AdvanceUntil/WaitUntil), but the
/// observation point is the FACADE's public event — not the pool's internal one.
///
/// State raise points along one reconnect iteration:
///   Disconnected (initial)
///   -> Connecting   (before ads.Connect())
///   -> Connected    (after the connection is published)
///   -> Disconnected (health-check failure / connect exception / teardown)
/// </summary>
public class AdsConnectionFacadeStateTests
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Health = TimeSpan.FromSeconds(5);

    // =====================================================================
    // Pool construction + helpers (mirrors AdsConnectionPoolStateTests /
    // AdsConnectionFacadeTests).
    // =====================================================================

    private static (AdsConnectionPool pool, FakeConnectionFactory factory, FakeTimeProvider time, AdsRouterReadySignal signal)
        CreatePool(params string[] plcIds)
    {
        if (plcIds.Length == 0) plcIds = ["plc1"];

        var targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in plcIds)
            targets[id] = new PlcTargetOptions { DisplayName = id, AmsNetId = "1.2.3.4.5.6", TimeoutMs = 5000 };

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

    private static AdsConnectionFacade FacadeOf(AdsConnectionPool pool, string plcId)
        => Assert.IsType<AdsConnectionFacade>(pool.GetConnection(plcId));

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

    /// <summary>
    /// Records every transition raised on the FACADE's
    /// <c>ConnectionStateChanged</c> event in arrival order, behind a lock so a
    /// test can advance fake time on the pool thread and read snapshots safely.
    /// </summary>
    private sealed class FacadeStateRecorder
    {
        private readonly object _gate = new();
        private readonly List<(ConnectionState Prev, ConnectionState State)> _events = [];

        public void Attach(IAdsConnection connection)
            => connection.ConnectionStateChanged += (_, e) =>
            {
                lock (_gate) { _events.Add((e.PreviousState, e.State)); }
            };

        public List<(ConnectionState Prev, ConnectionState State)> Pairs()
        {
            lock (_gate) { return [.. _events]; }
        }

        public List<ConnectionState> States()
        {
            lock (_gate) { return [.. _events.Select(e => e.State)]; }
        }

        public int CountOf(ConnectionState state)
        {
            lock (_gate) { return _events.Count(e => e.State == state); }
        }
    }

    // =====================================================================
    // 1. State property — initial value + reachable through the interface.
    // =====================================================================

    [Fact]
    public async Task FacadeState_InitiallyDisconnected()
    {
        // C14 wires a logger into the facade via the pool, so build the facade
        // through the pool rather than constructing it directly. No connection is
        // ever published (we never enqueue one / never start), so the facade stays
        // in its initial Disconnected state.
        var (pool, _, _, _) = CreatePool("plc1");

        // GetConnection returns IAdsConnection — the State property must be
        // reachable through the interface-typed reference, not just the concrete.
        IAdsConnection connection = pool.GetConnection("plc1");
        Assert.IsType<AdsConnectionFacade>(connection);

        Assert.Equal(ConnectionState.Disconnected, connection.State);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    // =====================================================================
    // 2. Event ordering — full outage/recovery cycle observed via the facade.
    // =====================================================================

    [Fact]
    public async Task FacadeStateChanged_FullOutageCycleViaPool_RaisesOrderedTransitions()
    {
        var (pool, factory, time, signal) = CreatePool("plc1");

        // Subscribe through the FACADE's IAdsConnection event — NOT the pool's
        // internal ConnectionStateChanged.
        IAdsConnection connection = pool.GetConnection("plc1");
        var rec = new FacadeStateRecorder();
        rec.Attach(connection);

        var first = new FakeManagedConnection("plc1");
        first.IsAliveResults.Enqueue(false); // first health check fails -> rebuild
        factory.Enqueue(first);

        var second = new FakeManagedConnection("plc1");
        factory.Enqueue(second);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await first.ConnectCalled.WaitAsync(RealTimeout);
        await WaitUntil(() => rec.States().Contains(ConnectionState.Connected));

        // Drive the health interval until IsAliveAsync fires (returns false), then
        // keep advancing until the rebuild reaches Connected again.
        await AdvanceUntil(
            time,
            () => rec.CountOf(ConnectionState.Connected) >= 2,
            Health);

        await second.ConnectCalled.WaitAsync(RealTimeout);

        var pairs = rec.Pairs();
        // The same ordered cycle as the pool-level
        // HealthCheckFailureThenRecovery_* test, but observed via the facade:
        // (Disconnected, Connecting), (Connecting, Connected),
        // (Connected, Disconnected), (Disconnected, Connecting),
        // (Connecting, Connected).
        Assert.Equal(
            [
                (ConnectionState.Disconnected, ConnectionState.Connecting),
                (ConnectionState.Connecting, ConnectionState.Connected),
                (ConnectionState.Connected, ConnectionState.Disconnected),
                (ConnectionState.Disconnected, ConnectionState.Connecting),
                (ConnectionState.Connecting, ConnectionState.Connected),
            ],
            pairs);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    // =====================================================================
    // 3. State property — tracks the same cycle at each sync point.
    // =====================================================================

    [Fact]
    public async Task FacadeState_TracksFullCycle_DisconnectedConnectedDisconnectedConnected()
    {
        var (pool, factory, time, signal) = CreatePool("plc1");

        IAdsConnection connection = pool.GetConnection("plc1");

        var first = new FakeManagedConnection("plc1");
        first.IsAliveResults.Enqueue(false); // first health check fails -> rebuild
        factory.Enqueue(first);

        var second = new FakeManagedConnection("plc1");
        factory.Enqueue(second);

        // Pre-start: nothing has connected, State must read Disconnected.
        Assert.Equal(ConnectionState.Disconnected, connection.State);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        // After the first successful connect.
        await first.ConnectCalled.WaitAsync(RealTimeout);
        await WaitUntil(() => connection.State == ConnectionState.Connected);
        Assert.Equal(ConnectionState.Connected, connection.State);

        // During the outage triggered by the failed health check (before the
        // rebuild publishes the second connection). Advance fake time to trigger
        // the health-check interval; stop as soon as Disconnected is observed.
        await AdvanceUntil(time, () => connection.State == ConnectionState.Disconnected, Health);
        Assert.Equal(ConnectionState.Disconnected, connection.State);

        // After the rebuild reconnects — keep advancing through the grace period
        // and reconnect backoff until the second connection is published.
        await AdvanceUntil(time, () => connection.State == ConnectionState.Connected, Health);
        await second.ConnectCalled.WaitAsync(RealTimeout);
        Assert.Equal(ConnectionState.Connected, connection.State);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    // =====================================================================
    // 4. Handler exceptions on the facade event must not break the pool loop.
    // =====================================================================

    [Fact]
    public async Task FacadeStateChanged_HandlerException_DoesNotBreakPoolLoop()
    {
        var (pool, factory, time, signal) = CreatePool("plc1");

        IAdsConnection connection = pool.GetConnection("plc1");

        // A throwing handler subscribed to the facade event must not kill the
        // pool's reconnect loop. A separate recorder proves the loop kept going.
        var recorded = new ConcurrentQueue<ConnectionState>();
        connection.ConnectionStateChanged += (_, e) =>
        {
            recorded.Enqueue(e.State);
            throw new InvalidOperationException("facade handler boom");
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
        await WaitUntil(() => recorded.Contains(ConnectionState.Connected));

        // Health failure -> rebuild still proceeds to a second connect, and the
        // second Connected still reaches the recorder (proving the loop survived
        // the handler throwing on every transition).
        await AdvanceUntil(
            time,
            () => recorded.Count(s => s == ConnectionState.Connected) >= 2,
            Health);

        await second.ConnectCalled.WaitAsync(RealTimeout);
        Assert.Equal(2, factory.CreateCount); // second connect actually happened
        Assert.Equal(ConnectionState.Connected, connection.State);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    // =====================================================================
    // 5. PreviousState chains correctly across the whole cycle.
    // =====================================================================

    [Fact]
    public async Task FacadeStateChanged_PreviousState_IsCorrectAcrossCycle()
    {
        var (pool, factory, time, signal) = CreatePool("plc1");

        IAdsConnection connection = pool.GetConnection("plc1");
        var rec = new FacadeStateRecorder();
        rec.Attach(connection);

        var first = new FakeManagedConnection("plc1");
        first.IsAliveResults.Enqueue(false); // health check fails -> rebuild
        factory.Enqueue(first);

        var second = new FakeManagedConnection("plc1");
        factory.Enqueue(second);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await first.ConnectCalled.WaitAsync(RealTimeout);
        await WaitUntil(() => rec.States().Contains(ConnectionState.Connected));
        await AdvanceUntil(time, () => rec.CountOf(ConnectionState.Connected) >= 2, Health);
        await second.ConnectCalled.WaitAsync(RealTimeout);

        var pairs = rec.Pairs();
        Assert.NotEmpty(pairs);

        // The very first transition leaves the implicit Disconnected start.
        Assert.Equal(ConnectionState.Disconnected, pairs[0].Prev);

        // Each event's PreviousState must equal the prior event's State — the
        // transitions chain without gaps or torn snapshots.
        for (int i = 1; i < pairs.Count; i++)
            Assert.Equal(pairs[i - 1].State, pairs[i].Prev);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    // =====================================================================
    // 6. SimulatedAdsConnection — always reports Connected.
    // =====================================================================

    [Fact]
    public void SimulatedConnection_StateIsConnected()
    {
        var conn = new SimulatedAdsConnection("plc1", "PLC One", NullLoggerFactory.Instance);

        Assert.Equal(ConnectionState.Connected, conn.State);

        // SimulatedAdsConnection implements IManagedConnection (: IAdsConnection),
        // so the State must read identically through the interface reference.
        IAdsConnection asInterface = conn;
        Assert.Equal(ConnectionState.Connected, asInterface.State);
    }

    // =====================================================================
    // 7. SimulatedAdsConnection — subscribing to the event is harmless.
    // =====================================================================

    [Fact]
    public async Task SimulatedConnection_EventSubscription_IsHarmless()
    {
        var conn = new SimulatedAdsConnection("plc1", "PLC One", NullLoggerFactory.Instance);

        var fired = 0;
        // Subscribing must not throw, and the simulated connection — being
        // permanently Connected — must never raise a transition.
        var exception = Record.Exception(() =>
        {
            conn.ConnectionStateChanged += (_, _) => Interlocked.Increment(ref fired);
        });
        Assert.Null(exception);

        // Give any (incorrect) async raise a real-time window to surface.
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.Equal(0, Volatile.Read(ref fired));
    }

    // =====================================================================
    // 8. Multiple handlers — one throwing must not skip the others.
    // =====================================================================

    [Fact]
    public async Task FacadeStateChanged_MultipleHandlers_AllInvoked_OneThrowDoesntSkipOthers()
    {
        var (pool, factory, _, signal) = CreatePool("plc1");

        IAdsConnection connection = pool.GetConnection("plc1");

        // Handler 1 throws on every transition; handler 2 records invocations.
        // Handler 2 must still be invoked despite handler 1 throwing.
        connection.ConnectionStateChanged += (_, _)
            => throw new InvalidOperationException("handler 1 boom");

        var handler2States = new ConcurrentQueue<ConnectionState>();
        connection.ConnectionStateChanged += (_, e) => handler2States.Enqueue(e.State);

        var conn = factory.Enqueue(new FakeManagedConnection("plc1"));

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await conn.ConnectCalled.WaitAsync(RealTimeout);
        await WaitUntil(() => handler2States.Contains(ConnectionState.Connected));

        // Handler 2 saw the Connecting -> Connected progression even though
        // handler 1 threw on each transition.
        Assert.Contains(ConnectionState.Connecting, handler2States);
        Assert.Contains(ConnectionState.Connected, handler2States);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }
}
