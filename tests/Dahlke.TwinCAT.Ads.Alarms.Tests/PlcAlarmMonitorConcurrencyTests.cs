using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Alarms.Tests;

/// <summary>
/// Tests for <see cref="PlcAlarmMonitor"/>'s lifecycle and concurrency behaviour:
/// registration against an unreachable target, deferred retry, disposal races, and
/// per-target delivery ordering.
/// </summary>
/// <remarks>
/// <para>
/// The facade's FIRST subscription registration is not durable: with no connection it
/// waits out <c>TimeoutMs</c> and throws <see cref="AdsConnectionUnavailableException"/>,
/// and <c>DurableSubscriptionRegistry.AddAsync</c> rolls the record back, so nothing is
/// retained for a later reconnect. Letting that escape <c>StartAsync</c> would fail
/// hosted-service startup and take the whole host down over ONE offline PLC.
/// </para>
/// <para>
/// A simulated connection is permanently connected and cannot produce that state, nor can
/// it hand a test control of which thread a notification arrives on. These tests drive the
/// monitor through a stub pool instead, which is also the only way to raise
/// <c>ConnectionStateChanged</c> on demand.
/// </para>
/// </remarks>
public class PlcAlarmMonitorConcurrencyTests
{
    private const string PlcId = "plc1";
    private const string Path = "GVL.Errors";

    private static PlcAlarmMonitor MonitorFor(StubPool pool, params string[] plcIds)
    {
        var options = new PlcAlarmsOptions();

        foreach (var plcId in plcIds.Length == 0 ? [PlcId] : plcIds)
            options.Targets[plcId] = new PlcAlarmTargetOptions { SymbolPath = Path, CycleTimeMs = 50 };

        return new PlcAlarmMonitor(
            pool,
            NullAlarmTextCatalog.Instance,
            Options.Create(options),
            NullLogger<PlcAlarmMonitor>.Instance);
    }

    private static object?[] OneAlarm(bool isActive = true, bool isAcked = false) =>
    [
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["sKey"] = "BMK1Err404",
            ["Id"] = "BMK1",
            ["ErrorCode"] = 404u,
            ["ErrorType"] = 3,
            ["IsActive"] = isActive,
            ["NeedsAck"] = true,
            ["IsAcked"] = isAcked,
            ["PLCTimeStamp"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["wYear"] = (ushort)2026, ["wMonth"] = (ushort)6, ["wDayOfWeek"] = (ushort)3,
                ["wDay"] = (ushort)17, ["wHour"] = (ushort)12, ["wMinute"] = (ushort)0,
                ["wSecond"] = (ushort)0, ["wMilliseconds"] = (ushort)0,
            },
        },
    ];

    [Fact]
    public async Task UnreachableTargetAtStartup_DoesNotStopTheHost()
    {
        var pool = new StubPool(failFirstSubscribe: true);
        using var monitor = MonitorFor(pool);

        // The whole point: this must not throw. It did before — one offline PLC failed
        // IHostedService startup and took down monitoring of every PLC that was up.
        await monitor.StartAsync(CancellationToken.None);

        Assert.Equal(1, pool.Connection.SubscribeAttempts);
        Assert.Empty(monitor.GetOutstanding());
    }

    [Fact]
    public async Task StartAsync_AttemptsEveryTargetConcurrently()
    {
        // Serially, each unreachable target burns its whole TimeoutMs before the next one
        // is even tried — ten offline PLCs delayed hosted-service startup by ~50 s at the
        // default 5 s. Every subscribe is parked here, so a serial StartAsync could only
        // ever have ONE attempt outstanding and this wait would time out.
        string[] plcIds = ["plc1", "plc2", "plc3"];
        var pool = new StubPool(failFirstSubscribe: false);
        using var monitor = MonitorFor(pool, plcIds);

        foreach (var plcId in plcIds)
            pool.ConnectionFor(plcId).BlockNextSubscribe();

        var starting = monitor.StartAsync(CancellationToken.None);

        await WaitForAsync(() => plcIds.All(id => pool.ConnectionFor(id).SubscribeAttempts == 1));

        foreach (var plcId in plcIds)
            pool.ConnectionFor(plcId).ReleaseBlockedSubscribe();

        await starting;

        // And all three really registered — a StartAsync that abandoned the parked
        // attempts would satisfy the wait above and still monitor nothing.
        Assert.All(plcIds, id => Assert.Equal(1, pool.ConnectionFor(id).RegistrationsCreated));
        Assert.All(plcIds, id => Assert.Equal(1, pool.ConnectionFor(id).LiveSubscriptions));
    }

    [Fact]
    public async Task StartAsync_ConcurrentAttempts_StillArmRetryPerFailedTargetOnly()
    {
        // The retry arming was written under a serial loop. Concurrently it must still be
        // per target: the failed one defers and later registers, the healthy one is left
        // alone and must NOT register a second time when its connection reports Connected.
        string[] plcIds = ["plc1", "plc2"];
        var pool = new StubPool(failFirstSubscribe: true);
        using var monitor = MonitorFor(pool, plcIds);

        // plc2 succeeds first time; plc1 fails once, exactly like an unreachable target.
        pool.ConnectionFor("plc2").SucceedEveryAttempt();

        await monitor.StartAsync(CancellationToken.None);

        Assert.Equal(1, pool.ConnectionFor("plc1").SubscribeAttempts);
        Assert.Equal(0, pool.ConnectionFor("plc1").RegistrationsCreated);
        Assert.Equal(1, pool.ConnectionFor("plc2").RegistrationsCreated);

        pool.ConnectionFor("plc1").RaiseConnected();
        pool.ConnectionFor("plc2").RaiseConnected();
        await WaitForAsync(() => pool.ConnectionFor("plc1").RegistrationsCreated == 1);
        await Task.Delay(50);

        // plc2's retry handler was detached on its successful first registration; a second
        // registration there would deliver every notification twice.
        Assert.Equal(1, pool.ConnectionFor("plc2").SubscribeAttempts);

        pool.ConnectionFor("plc1").Notify(OneAlarm());
        Assert.Single(monitor.GetOutstanding());
    }

    [Fact]
    public async Task StartAsync_AFailureThatIsNotUnreachability_StillBringsTheHostDown()
    {
        // A bad SymbolPath is a fault no reconnect will fix — README and CHANGELOG both
        // promise it still fails startup. Only AdsConnectionUnavailableException is caught.
        var pool = new StubPool(failFirstSubscribe: false);
        using var monitor = MonitorFor(pool);

        pool.Connection.FailEveryAttemptWith(() => new InvalidOperationException("no such symbol"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => monitor.StartAsync(CancellationToken.None));

        // A single fault propagates unwrapped, exactly as it did when startup was serial.
        Assert.Equal("no such symbol", ex.Message);
    }

    [Fact]
    public async Task StartAsync_TwoUnexpectedFailures_ReportBoth()
    {
        // Serially the second target was never even attempted, so only one fault could
        // exist. Concurrently both do, and awaiting Task.WhenAll rethrows only the FIRST —
        // reporting one and discarding the other would send an operator round the startup
        // loop once per misconfigured target.
        string[] plcIds = ["plc1", "plc2"];
        var pool = new StubPool(failFirstSubscribe: false);
        using var monitor = MonitorFor(pool, plcIds);

        foreach (var plcId in plcIds)
        {
            var id = plcId;
            pool.ConnectionFor(id).FailEveryAttemptWith(() => new InvalidOperationException($"no such symbol on {id}"));
        }

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => monitor.StartAsync(CancellationToken.None));

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.All(plcIds, id => Assert.Contains(
            ex.InnerExceptions, inner => inner.Message.Contains(id, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Dispose_CompletesTheTransitionsStream()
    {
        // Disposing the Subject without completing it first makes shutdown look like
        // silence to every subscriber: anything composing the stream (TakeUntil, ToTask,
        // an await foreach) waits forever for a terminal message that never arrives.
        var pool = new StubPool(failFirstSubscribe: false);
        var monitor = MonitorFor(pool);
        await monitor.StartAsync(CancellationToken.None);

        var completed = false;
        using var subscription = monitor.Transitions.Subscribe(_ => { }, () => completed = true);

        monitor.Dispose();

        Assert.True(completed);

        // The second Dispose must NOT reach the now-disposed Subject — OnCompleted on a
        // disposed Subject throws ObjectDisposedException.
        monitor.Dispose();
    }

    [Fact]
    public async Task DeferredTarget_RegistersWhenTheConnectionComesUp()
    {
        var pool = new StubPool(failFirstSubscribe: true);
        using var monitor = MonitorFor(pool);

        await monitor.StartAsync(CancellationToken.None);
        Assert.Empty(monitor.GetOutstanding());

        pool.Connection.RaiseConnected();
        await WaitForAsync(() => pool.Connection.SubscribeAttempts == 2);

        // Registered for real: a notification now flows all the way through.
        pool.Connection.Notify(OneAlarm());

        Assert.Equal("BMK1Err404", Assert.Single(monitor.GetOutstanding()).Key);
    }

    [Fact]
    public async Task TargetThatSucceedsFirstTime_NeverRegistersTwice()
    {
        var pool = new StubPool(failFirstSubscribe: false);
        using var monitor = MonitorFor(pool);

        await monitor.StartAsync(CancellationToken.None);
        Assert.Equal(1, pool.Connection.SubscribeAttempts);

        // The facade restores established subscriptions across reconnects itself. A retry
        // handler left armed here would register a SECOND time and every notification
        // would be delivered twice.
        pool.Connection.RaiseConnected();
        pool.Connection.RaiseConnected();
        await Task.Delay(50);

        Assert.Equal(1, pool.Connection.SubscribeAttempts);
    }

    [Fact]
    public async Task DeferredTarget_RegistersOnceWhenTwoConnectedTransitionsRace()
    {
        var pool = new StubPool(failFirstSubscribe: true);
        using var monitor = MonitorFor(pool);

        await monitor.StartAsync(CancellationToken.None);

        // Both raised before either retry can finish — the pool's loop can genuinely
        // deliver two transitions concurrently.
        await Task.WhenAll(
            Task.Run(() => pool.Connection.RaiseConnected()),
            Task.Run(() => pool.Connection.RaiseConnected()));

        await WaitForAsync(() => pool.Connection.SubscribeAttempts >= 2);
        await Task.Delay(50);

        // One failed attempt at startup plus exactly one successful retry.
        Assert.Equal(2, pool.Connection.SubscribeAttempts);

        pool.Connection.Notify(OneAlarm());
        Assert.Single(monitor.GetOutstanding());
    }

    [Fact]
    public async Task DisposeDuringAPendingRetry_LeavesNothingSubscribed()
    {
        var pool = new StubPool(failFirstSubscribe: true);
        var monitor = MonitorFor(pool);

        await monitor.StartAsync(CancellationToken.None);

        // Hold the retry inside SubscribeAsync so disposal lands while it is in flight.
        pool.Connection.BlockNextSubscribe();
        pool.Connection.RaiseConnected();
        await WaitForAsync(() => pool.Connection.SubscribeAttempts == 2);

        monitor.Dispose();
        pool.Connection.ReleaseBlockedSubscribe();

        // The registration really was handed out — and then disposed by whoever received
        // it, because nothing else holds the handle. Asserting the live count alone would
        // pass even if the subscribe had never completed.
        await WaitForAsync(() => pool.Connection.RegistrationsCreated == 1);
        await WaitForAsync(() => pool.Connection.LiveSubscriptions == 0);

        // Sequential double-dispose stays safe.
        monitor.Dispose();
    }

    [Fact]
    public async Task DisposeWhileStartAsyncIsStillSubscribing_DisposesTheLateRegistration()
    {
        var pool = new StubPool(failFirstSubscribe: false);
        var monitor = MonitorFor(pool);

        pool.Connection.BlockNextSubscribe();
        var starting = monitor.StartAsync(CancellationToken.None);
        await WaitForAsync(() => pool.Connection.SubscribeAttempts == 1);

        monitor.Dispose();
        pool.Connection.ReleaseBlockedSubscribe();
        await starting;

        Assert.Equal(1, pool.Connection.RegistrationsCreated);
        await WaitForAsync(() => pool.Connection.LiveSubscriptions == 0);
    }

    [Fact]
    public async Task OverlappingNotifications_CannotPublishOutOfOrder()
    {
        var pool = new StubPool(failFirstSubscribe: false);
        using var monitor = MonitorFor(pool);
        await monitor.StartAsync(CancellationToken.None);

        var delivered = new List<AlarmTransitionKind>();
        using var insideFirstHandler = new ManualResetEventSlim();
        using var secondNotifyStarted = new ManualResetEventSlim();
        var deliveredWhileFirstHandlerRan = -1;

        monitor.AlarmChanged += (_, transition) =>
        {
            lock (delivered)
                delivered.Add(transition.Kind);

            if (transition.Kind is not AlarmTransitionKind.Raised)
                return;

            // Hold the first delivery open while a second notification for the SAME
            // target is pushed from another thread. Publishing outside the per-target
            // lock would let that second snapshot be applied AND delivered during this
            // window, so a consumer folding the stream would end on Raised after Ended.
            insideFirstHandler.Set();
            secondNotifyStarted.Wait(TimeSpan.FromSeconds(5));
            Thread.Sleep(200);

            lock (delivered)
                deliveredWhileFirstHandlerRan = delivered.Count;
        };

        var first = Task.Run(() => pool.Connection.Notify(OneAlarm()));
        Assert.True(insideFirstHandler.Wait(TimeSpan.FromSeconds(5)));

        var second = Task.Run(() =>
        {
            secondNotifyStarted.Set();
            pool.Connection.Notify(OneAlarm(isActive: false, isAcked: true));
        });

        await Task.WhenAll(first, second);

        // The second snapshot could not be delivered while the first was still being
        // delivered: only the Raised itself had been counted.
        Assert.Equal(1, deliveredWhileFirstHandlerRan);

        lock (delivered)
        {
            Assert.Equal(AlarmTransitionKind.Raised, delivered[0]);
            Assert.Contains(AlarmTransitionKind.Ended, delivered);
            Assert.Equal(
                delivered.Count - 1, delivered.LastIndexOf(AlarmTransitionKind.Ended));
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);

        Assert.True(condition(), "Condition was not satisfied within the timeout.");
    }

    /// <summary>
    /// A pool serving one <see cref="StubConnection"/> per target id, created on first
    /// request. <see cref="Connection"/> is the single-target tests' one connection.
    /// </summary>
    private sealed class StubPool(bool failFirstSubscribe) : IAdsConnectionPool
    {
        private readonly ConcurrentDictionary<string, StubConnection> _connections =
            new(StringComparer.OrdinalIgnoreCase);

        public StubConnection Connection => ConnectionFor(PlcId);

        public StubConnection ConnectionFor(string plcId) =>
            _connections.GetOrAdd(plcId, id => new StubConnection(id, failFirstSubscribe));

        public IAdsConnection GetConnection(string plcId) => ConnectionFor(plcId);

        public bool TryGetConnection(string plcId, [NotNullWhen(true)] out IAdsConnection? connection)
        {
            connection = ConnectionFor(plcId);
            return true;
        }

        public IReadOnlyDictionary<string, IAdsConnection> GetAllConnections() =>
            _connections.ToDictionary(pair => pair.Key, pair => (IAdsConnection)pair.Value);

        public void ForceReconnect(string plcId) => throw new NotSupportedException();
        public IReadOnlyList<PlcTargetStatus> GetTargetStates() => throw new NotSupportedException();

        public bool TryGetSimulatedConnection(
            string plcId, [NotNullWhen(true)] out SimulatedAdsConnection? simulated)
        {
            simulated = null;
            return false;
        }
    }

    /// <summary>
    /// A connection whose first <c>SubscribeAsync</c> can be made to fail the way an
    /// unreachable target's facade does, and whose notifications and state transitions can
    /// be driven on demand.
    /// </summary>
    private sealed class StubConnection(string plcId, bool failFirstSubscribe) : IAdsConnection
    {
        private readonly List<Action<string, object?>> _callbacks = [];
        private TaskCompletionSource? _block;
        private Func<Exception>? _failure;
        private int _forcedSuccess;
        private int _subscribeAttempts;
        private int _registrationsCreated;
        private int _liveSubscriptions;

        public int SubscribeAttempts => Volatile.Read(ref _subscribeAttempts);

        /// <summary>
        /// Handles handed out over this connection's lifetime. Distinct from
        /// <see cref="LiveSubscriptions"/> so a test can tell "created then disposed"
        /// apart from "never created" — both leave the live count at zero.
        /// </summary>
        public int RegistrationsCreated => Volatile.Read(ref _registrationsCreated);

        public int LiveSubscriptions => Volatile.Read(ref _liveSubscriptions);

        public string PlcId => plcId;
        public string DisplayName => plcId;
        public bool IsConnected => true;
        public ConnectionState State => ConnectionState.Connected;

        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

        public void RaiseConnected() => ConnectionStateChanged?.Invoke(
            this,
            new ConnectionStateChangedEventArgs(
                plcId, ConnectionState.Connected, ConnectionState.Disconnected));

        /// <summary>
        /// Exempts this one connection from its pool's <c>failFirstSubscribe</c>, so a
        /// multi-target test can have one target fail while another succeeds.
        /// </summary>
        public void SucceedEveryAttempt() => Volatile.Write(ref _forcedSuccess, 1);

        /// <summary>
        /// Fails every <c>SubscribeAsync</c> with something OTHER than
        /// <see cref="AdsConnectionUnavailableException"/> — a bad symbol path, say — which
        /// the monitor must not swallow. A factory, so each call throws a fresh exception
        /// with its own stack.
        /// </summary>
        public void FailEveryAttemptWith(Func<Exception> failure) => Volatile.Write(ref _failure, failure);

        /// <summary>Parks <c>SubscribeAsync</c> until <see cref="ReleaseBlockedSubscribe"/>.</summary>
        /// <remarks>
        /// The gate is cleared by the RELEASE, never by the subscribe that observes it.
        /// Clearing it on observation left <c>ReleaseBlockedSubscribe</c> with nothing to
        /// complete, so the parked subscribe never resumed — one test hung and the other
        /// passed while asserting a count that had never been incremented.
        /// </remarks>
        public void BlockNextSubscribe() => Volatile.Write(
            ref _block, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void ReleaseBlockedSubscribe() => Interlocked.Exchange(ref _block, null)?.TrySetResult();

        /// <summary>Delivers a notification to every live subscription.</summary>
        public void Notify(object? value)
        {
            Action<string, object?>[] snapshot;
            lock (_callbacks)
                snapshot = [.. _callbacks];

            foreach (var callback in snapshot)
                callback(Path, value);
        }

        public async Task<IDisposable> SubscribeAsync(
            string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref _subscribeAttempts);

            var block = Volatile.Read(ref _block);
            if (block is not null)
                await block.Task.ConfigureAwait(false);

            if (Volatile.Read(ref _failure) is { } failure)
                throw failure();

            if (failFirstSubscribe && attempt == 1 && Volatile.Read(ref _forcedSuccess) == 0)
            {
                // Exactly what the facade throws once TimeoutMs elapses with no connection.
                throw new AdsConnectionUnavailableException(PlcId);
            }

            lock (_callbacks)
                _callbacks.Add(callback);

            Interlocked.Increment(ref _registrationsCreated);
            Interlocked.Increment(ref _liveSubscriptions);

            return new Registration(this, callback);
        }

        private sealed class Registration(StubConnection owner, Action<string, object?> callback)
            : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                lock (owner._callbacks)
                    owner._callbacks.Remove(callback);

                Interlocked.Decrement(ref owner._liveSubscriptions);
            }
        }

        // Nothing below is exercised by these tests; throwing beats returning a plausible
        // value that would let a future test pass for the wrong reason.
        public Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<AdsValueResult> ReadValueWithMetadataAsync(string symbolPath, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task WriteValueAsync(string symbolPath, object value, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, AdsValueResult>> ReadValuesAsync(
            IEnumerable<string> symbolPaths, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, AdsValueResult>> WriteValuesAsync(
            IReadOnlyDictionary<string, object?> values, CancellationToken ct) => throw new NotSupportedException();
        public Task<AdsRpcResult> InvokeRpcMethodAsync(
            string symbolPath, string methodName, object?[] parameters, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<AdsEnumMember>> GetEnumMembersAsync(string typeName, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<AdsState> GetAdsStateAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AdsDeviceInfo> GetDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task WriteControlAsync(AdsState state, ushort deviceState, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IDisposable> SubscribeAsync<T>(
            string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IDisposable> SubscribeAsync(
            string symbolPath, int cycleTimeMs, Action<AdsNotification> callback, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(
            string? parentPath, bool includeChildren, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdsSymbolInfo>> SearchSymbolsAsync(
            string pattern, bool includeChildren, CancellationToken ct) => throw new NotSupportedException();
    }
}
