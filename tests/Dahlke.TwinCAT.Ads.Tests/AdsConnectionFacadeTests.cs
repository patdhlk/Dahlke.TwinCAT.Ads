using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Tests for <see cref="AdsConnectionFacade"/>: the stable per-target
/// <see cref="IAdsConnection"/> that never changes identity for the pool's
/// lifetime and routes every operation to the current underlying
/// <see cref="IManagedConnection"/>. Split into pure unit tests (facade in
/// isolation, push model driven directly) and pool-integration tests (facade
/// wired through <see cref="AdsConnectionPool"/>).
/// </summary>
public class AdsConnectionFacadeTests
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Health = TimeSpan.FromSeconds(5);

    // =====================================================================
    // Pure unit tests — facade driven directly via SetCurrent/ClearCurrent.
    // =====================================================================

    [Fact]
    public async Task Operations_WithNoCurrentConnection_WaitThenThrow_WithPlcIdInMessage()
    {
        // TimeoutMs is the wait bound. With no connection ever published, each
        // operation waits the full TimeoutMs (of FAKE time) and then throws.
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1", new PlcTargetOptions { DisplayName = "PLC One", TimeoutMs = 1000 }, time);

        Assert.Equal("plc1", facade.PlcId);
        Assert.Equal("PLC One", facade.DisplayName);
        Assert.False(facade.IsConnected);

        // Start the op without awaiting; it parks waiting for a connection.
        var readTask = facade.ReadValueAsync("X", CancellationToken.None);
        Assert.False(readTask.IsCompleted);
        // Crossing TimeoutMs of fake time releases the wait with the unavailable throw.
        time.Advance(TimeSpan.FromMilliseconds(1000));
        var read = await Assert.ThrowsAsync<AdsConnectionUnavailableException>(() => readTask);
        Assert.Contains("plc1", read.Message);
        Assert.Equal("plc1", read.PlcId);

        await AssertWaitsThenThrows(facade.WriteValueAsync("X", 1, CancellationToken.None), time);
        await AssertWaitsThenThrows(facade.ReadValuesAsync(["X"], CancellationToken.None), time);
        await AssertWaitsThenThrows(facade.WriteValuesAsync(new() { ["X"] = 1 }, CancellationToken.None), time);
        await AssertWaitsThenThrows(facade.GetAdsStateAsync(CancellationToken.None), time);
        await AssertWaitsThenThrows(facade.SubscribeAsync("X", 100, (_, _) => { }, CancellationToken.None), time);

        static async Task AssertWaitsThenThrows(Task op, FakeTimeProvider time)
        {
            time.Advance(TimeSpan.FromMilliseconds(1000));
            await Assert.ThrowsAsync<AdsConnectionUnavailableException>(() => op);
        }
    }

    [Fact]
    public async Task Operations_WithCurrentConnection_DelegateToIt_AndPropagateResult()
    {
        var facade = new AdsConnectionFacade(
            "plc1", new PlcTargetOptions { DisplayName = "PLC One" }, new FakeTimeProvider());
        var underlying = new RecordingConnection("plc1");
        underlying.IsConnected = true;
        underlying.ReadResult = 42;

        facade.SetCurrent(underlying);

        Assert.True(facade.IsConnected);
        Assert.Same(underlying, facade.CurrentForTesting);

        var value = await facade.ReadValueAsync("MAIN.x", CancellationToken.None);
        Assert.Equal(42, value);
        Assert.Equal("MAIN.x", underlying.LastReadPath);

        await facade.WriteValueAsync("MAIN.y", 7, CancellationToken.None);
        Assert.Equal(("MAIN.y", (object)7), underlying.LastWrite);
    }

    [Fact]
    public void ClearCurrent_OnlyClearsMatchingInstance()
    {
        var facade = new AdsConnectionFacade("plc1", new PlcTargetOptions(), new FakeTimeProvider());
        var first = new RecordingConnection("plc1") { IsConnected = true };
        var second = new RecordingConnection("plc1") { IsConnected = true };

        facade.SetCurrent(first);
        facade.SetCurrent(second); // newer connection replaces the first

        // A stale teardown of the OLD connection must not blank the live one.
        facade.ClearCurrent(first);
        Assert.Same(second, facade.CurrentForTesting);
        Assert.True(facade.IsConnected);

        // Clearing the matching instance does clear it.
        facade.ClearCurrent(second);
        Assert.Null(facade.CurrentForTesting);
        Assert.False(facade.IsConnected);
    }

    [Fact]
    public void IsConnected_ReflectsUnderlyingIsConnected()
    {
        var facade = new AdsConnectionFacade("plc1", new PlcTargetOptions(), new FakeTimeProvider());
        var underlying = new RecordingConnection("plc1") { IsConnected = false };

        facade.SetCurrent(underlying);
        Assert.False(facade.IsConnected); // underlying reports not connected

        underlying.IsConnected = true;
        Assert.True(facade.IsConnected);

        facade.Clear();
        Assert.False(facade.IsConnected); // no current at all
    }

    // =====================================================================
    // Wait-then-throw mechanism — facade driven directly with FakeTimeProvider.
    // =====================================================================

    [Fact]
    public async Task FastPath_Connected_CompletesWithoutConsultingTheTimer()
    {
        // A FakeTimeProvider that is NEVER advanced: if the fast path touched the
        // timer/wait at all, the op would park forever and the await below would
        // hang past the real-time guard.
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade("plc1", new PlcTargetOptions(), time);
        var underlying = new RecordingConnection("plc1") { IsConnected = true, ReadResult = 7 };
        facade.SetCurrent(underlying);

        var value = await facade.ReadValueAsync("MAIN.x", CancellationToken.None).WaitAsync(RealTimeout);

        Assert.Equal(7, value);
        Assert.Equal("MAIN.x", underlying.LastReadPath);
    }

    [Fact]
    public async Task WaitThenSucceed_ConnectionPublishedMidWait_OperationProceeds()
    {
        // No time advance is needed: publication completes the waiter's TCS.
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1", new PlcTargetOptions { TimeoutMs = 5000 }, time);
        var arriving = new RecordingConnection("plc1") { IsConnected = true, ReadResult = 99 };

        // Start the op while disconnected; it parks waiting for a connection.
        var readTask = facade.ReadValueAsync("MAIN.n", CancellationToken.None);
        Assert.False(readTask.IsCompleted);

        // Publish mid-wait -> the parked op resumes against the new connection.
        facade.SetCurrent(arriving);

        var value = await readTask.WaitAsync(RealTimeout);
        Assert.Equal(99, value);
        Assert.Equal("MAIN.n", arriving.LastReadPath); // the new connection served the read
    }

    [Fact]
    public async Task WaitThenTimeout_HonorsConfiguredTimeoutMs()
    {
        // TimeoutMs=3000: at 2999ms still pending; crossing 3000ms faults.
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1", new PlcTargetOptions { TimeoutMs = 3000 }, time);

        var readTask = facade.ReadValueAsync("X", CancellationToken.None);
        Assert.False(readTask.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(2999));
        await Task.Yield();
        Assert.False(readTask.IsCompleted); // wait honors the CONFIGURED bound

        time.Advance(TimeSpan.FromMilliseconds(1)); // cross 3000ms
        var ex = await Assert.ThrowsAsync<AdsConnectionUnavailableException>(() => readTask);
        Assert.Equal("plc1", ex.PlcId);
    }

    [Fact]
    public async Task CancellationMidWait_ThrowsOperationCanceled_NotUnavailable()
    {
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1", new PlcTargetOptions { TimeoutMs = 5000 }, time);
        using var cts = new CancellationTokenSource();

        var readTask = facade.ReadValueAsync("X", cts.Token);
        Assert.False(readTask.IsCompleted);

        cts.Cancel(); // caller cancels mid-wait, well before TimeoutMs elapses

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    [Fact]
    public async Task MarkStopped_FailsFast_NoWait_MessageMentionsStopped()
    {
        // Never advanced: a stopped facade must NOT wait out TimeoutMs.
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1", new PlcTargetOptions { TimeoutMs = 60_000 }, time);

        facade.MarkStopped();

        var ex = await Assert.ThrowsAsync<AdsConnectionUnavailableException>(
            () => facade.ReadValueAsync("X", CancellationToken.None).WaitAsync(RealTimeout));
        Assert.Equal("plc1", ex.PlcId);
        Assert.Contains("stopped", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarkStopped_MidWait_WakesParkedWaiters_FailFast()
    {
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1", new PlcTargetOptions { TimeoutMs = 60_000 }, time);

        var readTask = facade.ReadValueAsync("X", CancellationToken.None);
        Assert.False(readTask.IsCompleted);

        facade.MarkStopped(); // no time advance: the waiter is woken immediately

        var ex = await Assert.ThrowsAsync<AdsConnectionUnavailableException>(
            () => readTask.WaitAsync(RealTimeout));
        Assert.Contains("stopped", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentWaiters_SinglePublish_BothComplete()
    {
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1", new PlcTargetOptions { TimeoutMs = 5000 }, time);
        var arriving = new RecordingConnection("plc1") { IsConnected = true, ReadResult = 5 };

        var a = facade.ReadValueAsync("A", CancellationToken.None);
        var b = facade.ReadValueAsync("B", CancellationToken.None);
        Assert.False(a.IsCompleted);
        Assert.False(b.IsCompleted);

        facade.SetCurrent(arriving); // one publish releases BOTH waiters

        await Task.WhenAll(a, b).WaitAsync(RealTimeout);
        Assert.Equal(5, await a);
        Assert.Equal(5, await b);
    }

    // =====================================================================
    // Pool-integration tests — facade wired through AdsConnectionPool.
    // =====================================================================

    private static (AdsConnectionPool pool, FakeConnectionFactory factory, FakeTimeProvider time, AdsRouterReadySignal signal)
        CreatePool(params string[] plcIds)
        => CreatePool(timeoutMs: 5000, plcIds);

    private static (AdsConnectionPool pool, FakeConnectionFactory factory, FakeTimeProvider time, AdsRouterReadySignal signal)
        CreatePool(int timeoutMs, params string[] plcIds)
    {
        if (plcIds.Length == 0) plcIds = ["plc1"];

        var targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in plcIds)
            targets[id] = new PlcTargetOptions { DisplayName = id, AmsNetId = "1.2.3.4.5.6", TimeoutMs = timeoutMs };

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

    private static AdsConnectionFacade FacadeOf(AdsConnectionPool pool, string plcId)
        => Assert.IsType<AdsConnectionFacade>(pool.GetConnection(plcId));

    private static async Task WaitForCurrent(AdsConnectionFacade facade, object expected)
    {
        var deadline = DateTime.UtcNow + RealTimeout;
        while (!ReferenceEquals(facade.CurrentForTesting, expected))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Facade never routed to the expected underlying connection.");
            await Task.Delay(TimeSpan.FromMilliseconds(5));
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

    [Fact]
    public async Task GetConnection_ReturnsFacade_BeforeAndAfterConnect_ThrowsForUnconfigured()
    {
        var (pool, factory, _, signal) = CreatePool("plc1");

        // C13: facades are created eagerly in the constructor — GetConnection is total
        // from construction, even BEFORE StartAsync is called.
        var facadePreStart = pool.GetConnection("plc1");
        Assert.NotNull(facadePreStart);
        Assert.IsType<AdsConnectionFacade>(facadePreStart);
        Assert.False(facadePreStart.IsConnected); // no loop started yet

        // Connect throws persistently, so the loop never publishes a live
        // connection — the facade stays in its "before first connect" outage
        // state for the lifetime of this test.
        var fail = factory.Enqueue(new FakeManagedConnection("plc1") { ConnectShouldThrow = true });

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        // Same facade instance returned post-start (identity is stable).
        var facade = pool.GetConnection("plc1");
        Assert.Same(facadePreStart, facade);
        Assert.IsType<AdsConnectionFacade>(facade);
        await fail.ConnectCalled.WaitAsync(RealTimeout);
        Assert.False(facade.IsConnected); // connect failed -> not connected

        // C13: unconfigured id throws UnknownPlcTargetException (no longer null).
        var ex = Assert.Throws<UnknownPlcTargetException>(() => pool.GetConnection("never-configured"));
        Assert.Equal("never-configured", ex.PlcId);
        Assert.Contains("plc1", ex.Message);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task GetAllConnections_ReturnsFacades_ForEveryConfiguredTarget_EvenBeforeConnect()
    {
        var (pool, _, _, signal) = CreatePool("plc1", "plc2", "plc3");

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        var all = pool.GetAllConnections();
        Assert.Equal(3, all.Count);
        Assert.All(all.Values, c => Assert.IsType<AdsConnectionFacade>(c));
        Assert.True(all.ContainsKey("plc1"));
        Assert.True(all.ContainsKey("plc2"));
        Assert.True(all.ContainsKey("plc3"));

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task FacadeIdentity_StableAcrossHealthCheckReconnect_RoutesToNewConnection()
    {
        var (pool, factory, time, signal) = CreatePool("plc1");

        var first = new FakeManagedConnection("plc1");
        first.IsAliveResults.Enqueue(false); // health check fails -> rebuild
        factory.Enqueue(first);
        var second = new FakeManagedConnection("plc1");
        factory.Enqueue(second);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        var facadeBefore = FacadeOf(pool, "plc1");
        await WaitForCurrent(facadeBefore, first);
        Assert.True(facadeBefore.IsConnected);

        // Drive health-check failure -> rebuild to the second connection.
        await AdvanceUntil(time, () => factory.CreateCount >= 2, Health);
        await WaitForCurrent(facadeBefore, second);

        var facadeAfter = pool.GetConnection("plc1");
        // SAME facade instance from GetConnection before and after the reconnect.
        Assert.Same(facadeBefore, facadeAfter);
        // ...but it now routes to a DIFFERENT underlying connection.
        Assert.Same(second, facadeBefore.CurrentForTesting);
        Assert.NotSame(first, second);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task Operations_DuringOutage_WaitThenThrowUnavailable_ThenSucceedAfterReconnect()
    {
        // Tiny per-target TimeoutMs (50ms) so the wait-then-throw window is far
        // shorter than the loop's MinReconnectDelay (2s): advancing 50ms releases
        // the wait WITHOUT also driving a reconnect, keeping the outage observable.
        var (pool, factory, time, signal) = CreatePool(timeoutMs: 50, "plc1");

        // First connect fails persistently for a while, then a healthy one.
        var fail = factory.Enqueue(new FakeManagedConnection("plc1") { ConnectShouldThrow = true });
        var healthy = new FakeManagedConnection("plc1");
        factory.Enqueue(healthy);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        var facade = FacadeOf(pool, "plc1");

        // Before any successful connect -> outage. The op now WAITS up to TimeoutMs
        // for a connection; start it, advance past TimeoutMs, then it throws.
        await fail.ConnectCalled.WaitAsync(RealTimeout);
        var readTask = facade.ReadValueAsync("X", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(50));
        var ex = await Assert.ThrowsAsync<AdsConnectionUnavailableException>(() => readTask);
        Assert.Contains("plc1", ex.Message);
        Assert.False(facade.IsConnected);

        // Drive time forward until the healthy connection is published.
        await AdvanceUntil(time, () => ReferenceEquals(facade.CurrentForTesting, healthy), Health);
        Assert.True(facade.IsConnected);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task ReconnectCycle_OperationIssuedDuringOutage_CompletesAgainstNewConnection()
    {
        // The marquee behavior of the redesign: a caller issues an operation while
        // the target is mid-outage; the pool's reconnect loop lands a NEW
        // connection during the wait window, and the in-flight op proceeds against
        // it — no throw, no torn snapshot. Generous TimeoutMs so the reconnect
        // (grace + backoff) completes well inside the wait window.

        // First connection connects, then fails its health check -> teardown.
        var first = new FakeManagedConnection("plc1");
        first.IsAliveResults.Enqueue(false); // health check fails -> rebuild
        // Second connection is a RecordingConnection so we can assert the read hit it.
        var second = new RecordingConnection("plc1") { IsConnected = true, ReadResult = 314 };
        var factory = new SequencedFactory(first, second);

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
            Options.Create(adsOptions),
            factory,
            signal,
            NullLogger<AdsConnectionPool>.Instance,
            time);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        var facade = FacadeOf(pool, "plc1");
        await WaitForCurrent(facade, first);
        Assert.True(facade.IsConnected);

        // Trigger the health-check failure: drive one health interval so the loop
        // observes IsAlive==false and tears the first connection down.
        time.Advance(Health);
        // The first connection is now being cleared; wait for the outage.
        await WaitUntil(() => facade.CurrentForTesting is null);

        // Issue the operation DURING the outage — it parks waiting for reconnection.
        var readTask = facade.ReadValueAsync("MAIN.v", CancellationToken.None);
        Assert.False(readTask.IsCompleted);

        // Drive the reconnect loop forward (grace period + backoff delay) until the
        // second connection is published into the facade.
        await AdvanceUntil(
            time,
            () => ReferenceEquals(facade.CurrentForTesting, second),
            TimeSpan.FromSeconds(1));

        // The parked op resumed against the NEW connection and completed.
        var value = await readTask.WaitAsync(RealTimeout);
        Assert.Equal(314, value);
        Assert.Equal("MAIN.v", second.LastReadPath);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task Operations_AfterStopAsync_ThrowUnavailable()
    {
        var (pool, factory, _, signal) = CreatePool("plc1");
        var conn = factory.Enqueue(new FakeManagedConnection("plc1"));

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        var facade = FacadeOf(pool, "plc1");
        await WaitForCurrent(facade, conn);
        Assert.True(facade.IsConnected);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);

        // After stop the facade is cleared: not connected, operations throw, but
        // the facade instance itself is still returned by GetConnection.
        Assert.False(facade.IsConnected);
        Assert.Same(facade, pool.GetConnection("plc1"));
        await Assert.ThrowsAsync<AdsConnectionUnavailableException>(
            () => facade.ReadValueAsync("X", CancellationToken.None));
    }

    [Fact]
    public async Task FacadeOperation_WhileConnected_DelegatesToCurrentManagedConnection()
    {
        // Use a recording connection so we can assert the read hit the underlying.
        var recording = new RecordingConnection("plc1") { ReadResult = 123 };
        var recordingFactory = new SingleConnectionFactory(recording);

        var adsOptions = new TwinCatAdsOptions
        {
            Targets = new(StringComparer.OrdinalIgnoreCase)
            {
                ["plc1"] = new PlcTargetOptions { DisplayName = "plc1", AmsNetId = "1.2.3.4.5.6" },
            },
        };
        var localTime = new FakeTimeProvider();
        var localSignal = new AdsRouterReadySignal();
        var localPool = new AdsConnectionPool(
            Options.Create(adsOptions),
            recordingFactory,
            localSignal,
            NullLogger<AdsConnectionPool>.Instance,
            localTime);

        localSignal.SetReady();
        await localPool.StartAsync(CancellationToken.None);

        var facade = Assert.IsType<AdsConnectionFacade>(localPool.GetConnection("plc1"));
        await WaitUntil(() => ReferenceEquals(facade.CurrentForTesting, recording));

        var value = await facade.ReadValueAsync("MAIN.n", CancellationToken.None);
        Assert.Equal(123, value);
        Assert.Equal("MAIN.n", recording.LastReadPath);

        await localPool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    // =====================================================================
    // Test doubles local to these tests.
    // =====================================================================

    /// <summary>
    /// A managed connection that records the operations the facade delegates to it
    /// and returns scripted results — used to assert routing, not lifecycle.
    /// </summary>
    private sealed class RecordingConnection : IManagedConnection
    {
        public RecordingConnection(string plcId) => PlcId = plcId;

        public string PlcId { get; }
        public string DisplayName => PlcId;
        public bool IsConnected { get; set; }

        public object? ReadResult { get; set; }
        public string? LastReadPath { get; private set; }
        public (string Path, object Value)? LastWrite { get; private set; }

        public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct)
        {
            LastReadPath = symbolPath;
            return Task.FromResult(ReadResult);
        }

        public Task WriteValueAsync(string symbolPath, object value, CancellationToken ct)
        {
            LastWrite = (symbolPath, value);
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, object?>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct)
            => Task.FromResult(new Dictionary<string, object?>());

        public Task WriteValuesAsync(Dictionary<string, object> values, CancellationToken ct)
            => Task.CompletedTask;

        public Task<AdsState> GetAdsStateAsync(CancellationToken ct)
            => Task.FromResult(default(AdsState));

        public Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct)
            => Task.FromResult<IDisposable>(new DummyDisposable());

        public void Connect() => IsConnected = true;
        public void Disconnect() => IsConnected = false;
        public Task<bool> IsAliveAsync(CancellationToken ct) => Task.FromResult(true);
        public void ForceDisconnect() => IsConnected = false;
        public void LogSymbolTree(SymbolDumpOptions options) { }
        public void Dispose() => IsConnected = false;

        private sealed class DummyDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Factory that hands out a fixed sequence of preset connections, then
    /// manufactures defaults once the script is drained. Used to script a
    /// reconnect (first -> second) across distinct connection instances.
    /// </summary>
    private sealed class SequencedFactory(params IManagedConnection[] connections) : IAdsConnectionFactory
    {
        private int _index;

        public IManagedConnection Create(string plcId, PlcTargetOptions options)
        {
            var i = Interlocked.Increment(ref _index) - 1;
            return i < connections.Length ? connections[i] : new FakeManagedConnection(plcId, options.DisplayName);
        }
    }

    /// <summary>Factory that hands out one preset connection, then defaults.</summary>
    private sealed class SingleConnectionFactory(IManagedConnection connection) : IAdsConnectionFactory
    {
        private int _handedOut;

        public IManagedConnection Create(string plcId, PlcTargetOptions options)
        {
            if (Interlocked.Exchange(ref _handedOut, 1) == 0)
                return connection;
            return new FakeManagedConnection(plcId, options.DisplayName);
        }
    }
}
