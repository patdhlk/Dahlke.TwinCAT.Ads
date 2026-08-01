using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// <see cref="IAdsConnection.WithTimeout"/> — the per-call bound that replaces the target's
/// configured <see cref="PlcTargetOptions.TimeoutMs"/> for one scoped view.
///
/// Coverage:
/// - The scope reaches the UNDERLYING connection, on every operation shape. This is the half a
///   facade-level implementation could not deliver: a facade that only raced the call against its
///   own timer could shorten a bound but never lengthen one, because the underlying connection
///   would still cancel at its own configured value first.
/// - The scope also bounds the facade's wait for a connection, in both directions — a shorter
///   scope gives up sooner than TimeoutMs, a longer one parks past it.
/// - Scopes replace rather than nest; identity, state and the state event pass through.
/// - Zero and negative are rejected at the boundary.
/// - The unscoped connection is unaffected by any scope taken over it.
/// </summary>
public class WithTimeoutScopeTests
{
    private static readonly TimeSpan Scope = TimeSpan.FromSeconds(30);

    private static (AdsConnectionFacade Facade, TimeoutRecordingConnection Underlying) Connected(
        int timeoutMs = 1000, int browseTimeoutMs = 30000)
    {
        var facade = new AdsConnectionFacade(
            "plc1",
            new PlcTargetOptions { DisplayName = "PLC One", TimeoutMs = timeoutMs, SymbolBrowseTimeoutMs = browseTimeoutMs },
            new FakeTimeProvider(),
            NullLogger.Instance);
        var underlying = new TimeoutRecordingConnection();
        facade.SetCurrent(underlying);
        return (facade, underlying);
    }

    // =========================================================================
    // The scope reaches the underlying connection
    // =========================================================================

    [Fact]
    public async Task ScopedRead_PassesTheTimeoutToTheUnderlyingConnection()
    {
        var (facade, underlying) = Connected();

        await facade.WithTimeout(Scope).ReadValueAsync<int>("MAIN.X");

        Assert.Equal(Scope, underlying.LastTimeout);
    }

    [Fact]
    public async Task UnscopedRead_PassesNoTimeout_SoTheTargetsConfiguredBoundApplies()
    {
        var (facade, underlying) = Connected();

        await facade.ReadValueAsync<int>("MAIN.X", CancellationToken.None);

        Assert.Null(underlying.LastTimeout);
    }

    [Fact]
    public async Task EveryOperationShape_CarriesTheScope()
    {
        var (facade, underlying) = Connected();
        var scoped = facade.WithTimeout(Scope);

        // One assertion per operation, because the plumbing is per-method: a shape that forgot to
        // forward the scope would otherwise hide behind the shapes that did.
        var calls = new List<(string Name, Func<Task> Invoke)>
        {
            ("ReadValueAsync<T>", () => scoped.ReadValueAsync<int>("MAIN.X")),
            ("ReadValueAsync", () => scoped.ReadValueAsync("MAIN.X")),
            ("ReadValueWithMetadataAsync", () => scoped.ReadValueWithMetadataAsync("MAIN.X")),
            ("WriteValueAsync<T>", () => scoped.WriteValueAsync("MAIN.X", 1)),
            ("WriteValueAsync", () => scoped.WriteValueAsync("MAIN.X", (object)1)),
            ("ReadValuesAsync", () => scoped.ReadValuesAsync(["MAIN.X"])),
            ("WriteValuesAsync", () => scoped.WriteValuesAsync(new Dictionary<string, object?> { ["MAIN.X"] = 1 })),
            ("InvokeRpcMethodAsync", () => scoped.InvokeRpcMethodAsync("MAIN.FB", "M", [])),
            ("GetEnumMembersAsync", () => scoped.GetEnumMembersAsync("E")),
            ("GetAdsStateAsync", () => scoped.GetAdsStateAsync()),
            ("GetDeviceInfoAsync", () => scoped.GetDeviceInfoAsync()),
            ("WriteControlAsync", () => scoped.WriteControlAsync(AdsState.Run, 0)),
            ("SubscribeAsync", () => scoped.SubscribeAsync("MAIN.X", 100, (_, _) => { })),
            ("SubscribeAsync<T>", () => scoped.SubscribeAsync<int>("MAIN.X", 100, (_, _) => { })),
            ("SubscribeAsync(notification)", () => scoped.SubscribeAsync("MAIN.X", 100, (AdsNotification _) => { })),
            ("GetSymbolTreeAsync", () => scoped.GetSymbolTreeAsync(null)),
            ("GetSymbolsAsync(includeChildren)", () => scoped.GetSymbolsAsync(null, false)),
            ("SearchSymbolsAsync", () => scoped.SearchSymbolsAsync("X", false)),
        };

        var notForwarded = new List<string>();
        foreach (var (name, invoke) in calls)
        {
            underlying.LastTimeout = null;
            await invoke();
            if (underlying.LastTimeout != Scope)
                notForwarded.Add(name);
        }

        Assert.Empty(notForwarded);
    }

    // =========================================================================
    // The scope bounds the wait for a connection — in both directions
    // =========================================================================

    [Fact]
    public async Task ShorterScope_GivesUpBeforeTheConfiguredTimeout()
    {
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1", new PlcTargetOptions { TimeoutMs = 10_000 }, time, NullLogger.Instance);

        var task = facade.WithTimeout(TimeSpan.FromSeconds(1)).ReadValueAsync<int>("MAIN.X");

        Assert.False(task.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<AdsConnectionUnavailableException>(() => task);
    }

    [Fact]
    public async Task LongerScope_KeepsWaitingPastTheConfiguredTimeout()
    {
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1", new PlcTargetOptions { TimeoutMs = 1_000 }, time, NullLogger.Instance);

        var task = facade.WithTimeout(TimeSpan.FromSeconds(30)).ReadValueAsync<int>("MAIN.X");

        // The configured bound comes and goes with the call still parked — the scope, not
        // TimeoutMs, is what governs.
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(task.IsCompleted);

        // A connection arriving inside the scope's window releases the parked call.
        facade.SetCurrent(new TimeoutRecordingConnection());
        Assert.Equal(0, await task);
    }

    [Fact]
    public async Task ScopeDoesNotLeakOntoTheUnscopedConnection()
    {
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1", new PlcTargetOptions { TimeoutMs = 1_000 }, time, NullLogger.Instance);

        _ = facade.WithTimeout(TimeSpan.FromSeconds(30));
        var unscoped = facade.ReadValueAsync<int>("MAIN.X", CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<AdsConnectionUnavailableException>(() => unscoped);
    }

    // =========================================================================
    // Composition and identity
    // =========================================================================

    [Fact]
    public async Task ScopesReplaceRatherThanNest()
    {
        var (facade, underlying) = Connected();

        await facade.WithTimeout(TimeSpan.FromSeconds(5)).WithTimeout(Scope).ReadValueAsync<int>("MAIN.X");

        Assert.Equal(Scope, underlying.LastTimeout);
    }

    [Fact]
    public void ScopedView_SharesIdentityAndStateWithTheConnectionItScopes()
    {
        var (facade, _) = Connected();

        var scoped = facade.WithTimeout(Scope);

        Assert.Equal(facade.PlcId, scoped.PlcId);
        Assert.Equal(facade.DisplayName, scoped.DisplayName);
        Assert.Equal(facade.IsConnected, scoped.IsConnected);
        Assert.Equal(facade.State, scoped.State);
    }

    [Fact]
    public void ScopedView_ForwardsTheConnectionStateEvent()
    {
        var (facade, _) = Connected();
        var scoped = facade.WithTimeout(Scope);
        ConnectionStateChangedEventArgs? seen = null;

        scoped.ConnectionStateChanged += (_, e) => seen = e;
        facade.OnStateChanged(new ConnectionStateChangedEventArgs("plc1", ConnectionState.Disconnected, ConnectionState.Connected));

        Assert.NotNull(seen);
        Assert.Equal(ConnectionState.Disconnected, seen!.State);
    }

    [Fact]
    public void ScopedView_UnsubscribesFromTheEventItForwarded()
    {
        var (facade, _) = Connected();
        var scoped = facade.WithTimeout(Scope);
        var count = 0;
        void Handler(object? _, ConnectionStateChangedEventArgs __) => count++;

        scoped.ConnectionStateChanged += Handler;
        facade.OnStateChanged(new ConnectionStateChangedEventArgs("plc1", ConnectionState.Disconnected, ConnectionState.Connected));
        scoped.ConnectionStateChanged -= Handler;
        facade.OnStateChanged(new ConnectionStateChangedEventArgs("plc1", ConnectionState.Connected, ConnectionState.Disconnected));

        Assert.Equal(1, count);
    }

    // =========================================================================
    // Boundary validation
    // =========================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTimeout_IsRejected(int seconds)
    {
        var (facade, _) = Connected();

        Assert.Throws<ArgumentOutOfRangeException>(() => facade.WithTimeout(TimeSpan.FromSeconds(seconds)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => facade.WithTimeout(Scope).WithTimeout(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void SimulatedConnection_RejectsNonPositiveTimeoutToo()
    {
        using var sim = new SimulatedAdsConnection("plc1", "Sim", NullLoggerFactory.Instance);

        Assert.Throws<ArgumentOutOfRangeException>(() => sim.WithTimeout(TimeSpan.Zero));
    }

    [Fact]
    public async Task SimulatedConnection_ScopeChangesNothingObservable()
    {
        using var sim = new SimulatedAdsConnection("plc1", "Sim", NullLoggerFactory.Instance);
        await sim.WriteValueAsync("MAIN.X", 7);

        var scoped = sim.WithTimeout(Scope);

        Assert.Equal("plc1", scoped.PlcId);
        Assert.Equal(7, await scoped.ReadValueAsync<int>("MAIN.X"));
    }

    /// <summary>
    /// Records the timeout the facade forwarded on the most recent call. Every operation answers
    /// a default rather than throwing, because this double exists to observe the ARGUMENT, not the
    /// result.
    /// </summary>
    private sealed class TimeoutRecordingConnection : IManagedConnection
    {
        public TimeSpan? LastTimeout { get; set; }

        public string PlcId => "plc1";
        public string DisplayName => "PLC One";
        public bool IsConnected => true;

        private T Record<T>(TimeSpan? timeout, T result)
        {
            LastTimeout = timeout;
            return result;
        }

        public Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct, TimeSpan? timeout = null)
            => Task.FromResult(Record(timeout, default(T)!));

        public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct, TimeSpan? timeout = null)
            => Task.FromResult(Record(timeout, (object?)null));

        public Task<AdsValueResult> ReadValueWithMetadataAsync(string symbolPath, CancellationToken ct, TimeSpan? timeout = null)
            => Task.FromResult(Record(timeout, AdsValueResult.Success(null, null)));

        public Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct, TimeSpan? timeout = null)
            => Record(timeout, Task.CompletedTask);

        public Task WriteValueAsync(string symbolPath, object value, CancellationToken ct, TimeSpan? timeout = null)
            => Record(timeout, Task.CompletedTask);

        public Task<IReadOnlyDictionary<string, AdsValueResult>> ReadValuesAsync(
            IEnumerable<string> symbolPaths, CancellationToken ct, TimeSpan? timeout = null)
            => Task.FromResult(Record(timeout, (IReadOnlyDictionary<string, AdsValueResult>)new Dictionary<string, AdsValueResult>()));

        public Task<IReadOnlyDictionary<string, AdsValueResult>> WriteValuesAsync(
            IReadOnlyDictionary<string, object?> values, CancellationToken ct, TimeSpan? timeout = null)
            => Task.FromResult(Record(timeout, (IReadOnlyDictionary<string, AdsValueResult>)new Dictionary<string, AdsValueResult>()));

        public Task<AdsRpcResult> InvokeRpcMethodAsync(
            string symbolPath, string methodName, object?[] parameters, CancellationToken ct, TimeSpan? timeout = null)
            => Task.FromResult(Record(timeout, new AdsRpcResult(null, [])));

        public Task<IReadOnlyList<AdsEnumMember>> GetEnumMembersAsync(string typeName, CancellationToken ct, TimeSpan? timeout = null)
            => Task.FromResult(Record(timeout, (IReadOnlyList<AdsEnumMember>)[]));

        public Task<AdsState> GetAdsStateAsync(CancellationToken ct, TimeSpan? timeout = null)
            => Task.FromResult(Record(timeout, AdsState.Run));

        public Task<AdsDeviceInfo> GetDeviceInfoAsync(CancellationToken ct, TimeSpan? timeout = null)
            => Task.FromResult(Record(timeout, new AdsDeviceInfo("dev", "1.0")));

        public Task WriteControlAsync(AdsState state, ushort deviceState, CancellationToken ct, TimeSpan? timeout = null)
            => Record(timeout, Task.CompletedTask);

        public Task<IDisposable> SubscribeAsync(
            string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct, TimeSpan? timeout = null)
            => Task.FromResult(Record(timeout, (IDisposable)new NoopDisposable()));

        public Task<IDisposable> SubscribeAsync<T>(
            string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct, TimeSpan? timeout = null)
            => Task.FromResult(Record(timeout, (IDisposable)new NoopDisposable()));

        public Task<IDisposable> SubscribeAsync(
            string symbolPath, int cycleTimeMs, Action<AdsNotification> callback, CancellationToken ct, TimeSpan? timeout = null)
            => Task.FromResult(Record(timeout, (IDisposable)new NoopDisposable()));

        public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolTreeAsync(string? parentPath, CancellationToken ct, TimeSpan? timeout = null)
            => Task.FromResult(Record(timeout, (IReadOnlyList<AdsSymbolInfo>)[]));

        public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(
            string? parentPath, bool includeChildren, CancellationToken ct, TimeSpan? timeout = null)
            => Task.FromResult(Record(timeout, (IReadOnlyList<AdsSymbolInfo>)[]));

        public Task<IReadOnlyList<AdsSymbolInfo>> SearchSymbolsAsync(
            string pattern, bool includeChildren, CancellationToken ct, TimeSpan? timeout = null)
            => Task.FromResult(Record(timeout, (IReadOnlyList<AdsSymbolInfo>)[]));

        public void Connect() { }
        public void Disconnect() { }
        public Task<bool> IsAliveAsync(CancellationToken ct) => Task.FromResult(true);
        public void ForceDisconnect() { }
        public void LogSymbolTree(SymbolDumpOptions options) { }
        public void Dispose() { }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
