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
    public async Task Operations_WithNoCurrentConnection_Throw_WithPlcIdInMessage()
    {
        var facade = new AdsConnectionFacade("plc1", new PlcTargetOptions { DisplayName = "PLC One" });

        Assert.Equal("plc1", facade.PlcId);
        Assert.Equal("PLC One", facade.DisplayName);
        Assert.False(facade.IsConnected);

        var read = await Assert.ThrowsAsync<AdsConnectionUnavailableException>(
            () => facade.ReadValueAsync("X", CancellationToken.None));
        Assert.Contains("plc1", read.Message);
        Assert.Equal("plc1", read.PlcId);

        await Assert.ThrowsAsync<AdsConnectionUnavailableException>(
            () => facade.WriteValueAsync("X", 1, CancellationToken.None));
        await Assert.ThrowsAsync<AdsConnectionUnavailableException>(
            () => facade.ReadValuesAsync(["X"], CancellationToken.None));
        await Assert.ThrowsAsync<AdsConnectionUnavailableException>(
            () => facade.WriteValuesAsync(new() { ["X"] = 1 }, CancellationToken.None));
        await Assert.ThrowsAsync<AdsConnectionUnavailableException>(
            () => facade.GetAdsStateAsync(CancellationToken.None));
        await Assert.ThrowsAsync<AdsConnectionUnavailableException>(
            () => facade.SubscribeAsync("X", 100, (_, _) => { }, CancellationToken.None));
    }

    [Fact]
    public async Task Operations_WithCurrentConnection_DelegateToIt_AndPropagateResult()
    {
        var facade = new AdsConnectionFacade("plc1", new PlcTargetOptions { DisplayName = "PLC One" });
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
        var facade = new AdsConnectionFacade("plc1", new PlcTargetOptions());
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
        var facade = new AdsConnectionFacade("plc1", new PlcTargetOptions());
        var underlying = new RecordingConnection("plc1") { IsConnected = false };

        facade.SetCurrent(underlying);
        Assert.False(facade.IsConnected); // underlying reports not connected

        underlying.IsConnected = true;
        Assert.True(facade.IsConnected);

        facade.Clear();
        Assert.False(facade.IsConnected); // no current at all
    }

    // =====================================================================
    // Pool-integration tests — facade wired through AdsConnectionPool.
    // =====================================================================

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
    public async Task GetConnection_ReturnsFacade_BeforeConnect_AndNullForUnconfigured()
    {
        var (pool, factory, _, signal) = CreatePool("plc1");

        // Before StartAsync there are no facades yet.
        Assert.Null(pool.GetConnection("plc1"));

        // Connect throws persistently, so the loop never publishes a live
        // connection — the facade stays in its "before first connect" outage
        // state for the lifetime of this test.
        var fail = factory.Enqueue(new FakeManagedConnection("plc1") { ConnectShouldThrow = true });

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        // Facade exists immediately, before any successful connect.
        var facade = pool.GetConnection("plc1");
        Assert.NotNull(facade);
        Assert.IsType<AdsConnectionFacade>(facade);
        await fail.ConnectCalled.WaitAsync(RealTimeout);
        Assert.False(facade!.IsConnected); // connect failed -> not connected

        // Unconfigured id -> null (unchanged this commit).
        Assert.Null(pool.GetConnection("never-configured"));

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
    public async Task Operations_DuringOutage_ThrowUnavailable_ThenSucceedAfterReconnect()
    {
        var (pool, factory, time, signal) = CreatePool("plc1");

        // First connect fails persistently for a while, then a healthy one.
        var fail = factory.Enqueue(new FakeManagedConnection("plc1") { ConnectShouldThrow = true });
        var healthy = new FakeManagedConnection("plc1");
        factory.Enqueue(healthy);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        var facade = FacadeOf(pool, "plc1");

        // Before any successful connect -> outage -> operations throw.
        await fail.ConnectCalled.WaitAsync(RealTimeout);
        var ex = await Assert.ThrowsAsync<AdsConnectionUnavailableException>(
            () => facade.ReadValueAsync("X", CancellationToken.None));
        Assert.Contains("plc1", ex.Message);
        Assert.False(facade.IsConnected);

        // Drive time forward until the healthy connection is published.
        await AdvanceUntil(time, () => ReferenceEquals(facade.CurrentForTesting, healthy), Health);
        Assert.True(facade.IsConnected);

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
