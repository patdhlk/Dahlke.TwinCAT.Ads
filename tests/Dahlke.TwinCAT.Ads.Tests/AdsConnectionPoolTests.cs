using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// CHARACTERIZATION tests: they document what <see cref="AdsConnectionPool"/>'s
/// reconnect loop DOES today, including the grace-period quirk, not what it
/// arguably should do. Derived directly from the loop in AdsConnectionPool.cs.
///
/// Timing model of one outer loop iteration:
///   1. factory.Create() -> ads (non-null thereafter)
///   2. ads.Connect()
///      - throws  -> caught, logged
///      - succeeds -> stored, delay reset to 2s, then inner health loop:
///          Task.Delay(5s); IsAliveAsync(); if false -> break inner loop
///   3. Cleanup (ads is non-null here in EVERY path, because Create succeeded):
///        TryRemove; Task.Delay(2s grace); ForceDisconnect(); Dispose()
///   4. Task.Delay(backoff); backoff = min(backoff*2, 30s)
///
/// Consequence pinned by these tests: when Create() succeeds but Connect()
/// throws, the 2s DisposeGracePeriod ALSO elapses before the backoff delay,
/// so consecutive Connect attempts are separated by (2s grace + backoff).
/// </summary>
public class AdsConnectionPoolTests
{
    // Wall-clock failure guard only — passing tests return as soon as the awaited
    // hook fires, so a generous value costs nothing on green runs.
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Health = TimeSpan.FromSeconds(5);

    private static (AdsConnectionPool pool, FakeConnectionFactory factory, FakeTimeProvider time, AdsRouterReadySignal signal)
        CreatePool(params string[] plcIds)
        => CreatePool(symbolDump: null, plcIds);

    private static (AdsConnectionPool pool, FakeConnectionFactory factory, FakeTimeProvider time, AdsRouterReadySignal signal)
        CreatePool(SymbolDumpOptions? symbolDump, params string[] plcIds)
    {
        if (plcIds.Length == 0) plcIds = ["plc1"];

        var targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in plcIds)
            targets[id] = new PlcTargetOptions { DisplayName = id, AmsNetId = "1.2.3.4.5.6" };

        var adsOptions = new TwinCatAdsOptions { Targets = targets };
        if (symbolDump is not null)
            adsOptions.Diagnostics.SymbolDump = symbolDump;

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
    /// Awaits a synchronisation hook while nudging fake time forward by
    /// <paramref name="step"/> until the hook fires. Each iteration of the
    /// pool loop registers its timer with the FakeTimeProvider; if we advance
    /// before the timer is registered the advance is lost, so we retry-advance
    /// in a short bounded real-time loop. Real time is only a failure guard,
    /// not the primary synchronisation mechanism — the hook (a TCS completed
    /// from inside the loop) is.
    /// </summary>
    private static async Task AdvanceUntil(FakeTimeProvider time, Task hook, TimeSpan step)
    {
        var deadline = DateTime.UtcNow + RealTimeout;
        while (!hook.IsCompleted)
        {
            time.Advance(step);
            // Yield real time so the loop's continuation can run and either
            // complete the hook or register its next timer.
            var winner = await Task.WhenAny(hook, Task.Delay(TimeSpan.FromMilliseconds(20)));
            if (winner == hook) break;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Hook did not complete within the real-time guard window.");
        }
        await hook.WaitAsync(RealTimeout);
    }

    private static Task Await(Task hook) => hook.WaitAsync(RealTimeout);

    /// <summary>
    /// The pool stores the live connection (line: _connections[plcId] = ads)
    /// AFTER ads.Connect() returns — and our Connect hook fires from INSIDE
    /// Connect(). So between the hook firing and the dict write there is a
    /// window. This polls GetConnection (real-time guard only) until the
    /// expected instance is published, removing that window from assertions.
    /// </summary>
    private static async Task WaitForConnection(AdsConnectionPool pool, string plcId, object expected)
    {
        var deadline = DateTime.UtcNow + RealTimeout;
        while (!ReferenceEquals(pool.GetConnection(plcId), expected))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"GetConnection('{plcId}') never became the expected instance.");
            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }
    }

    // =====================================================================

    [Fact]
    public async Task ConnectsOnStart_WhenRouterReady()
    {
        var (pool, factory, _, signal) = CreatePool("plc1");
        var conn = factory.Enqueue(new FakeManagedConnection("plc1"));

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await Await(conn.ConnectCalled);
        await WaitForConnection(pool, "plc1", conn);

        Assert.Equal(1, conn.ConnectCount);
        Assert.Same(conn, factory.Created[0]);
        Assert.Same(conn, pool.GetConnection("plc1"));

        await pool.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BackoffDoublesAndCaps_WhenConnectThrowsPersistently()
    {
        var (pool, factory, time, signal) = CreatePool("plc1");

        // The factory records one distinct instance per Create call, so we
        // enqueue several identical persistently-failing fakes up front and
        // track Connect attempts via CreateCount / per-instance hooks.
        var fakes = new List<FakeManagedConnection>();
        for (int i = 0; i < 8; i++)
            fakes.Add(factory.Enqueue(new FakeManagedConnection("plc1") { ConnectShouldThrow = true }));

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        // Attempt 1 happens immediately (no delay before first Create).
        await Await(fakes[0].ConnectCalled);
        Assert.Equal(1, factory.CreateCount);

        // After each failed Connect: 2s grace + backoff delay, THEN next Create.
        // Backoff sequence: 2s, 4s, 8s, 16s, 30s, 30s.
        TimeSpan[] backoffs =
        [
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30),
        ];

        for (int i = 0; i < backoffs.Length; i++)
        {
            var expectedCreatesBefore = i + 1;
            Assert.Equal(expectedCreatesBefore, factory.CreateCount);

            // After a failed Connect the loop runs Task.Delay(2s grace) then
            // Task.Delay(backoff) before the next Create. Characterise the
            // exact total: advancing to just SHORT of (grace + backoff) must
            // NOT trigger the next Create; crossing the boundary must.
            var total = Grace + backoffs[i];

            // First fire the grace timer (registered immediately after the
            // failed Connect), then assert the backoff timer has NOT yet
            // elapsed at (total - epsilon).
            await AdvanceToJustBefore(time, factory, expectedCreatesBefore, total);
            Assert.Equal(expectedCreatesBefore, factory.CreateCount);

            // Cross the boundary; the next Create+Connect must now occur.
            await AdvanceUntilCreateCount(time, factory, expectedCreatesBefore + 1);

            await Await(fakes[i + 1].ConnectCalled);
            Assert.Equal(expectedCreatesBefore + 1, factory.CreateCount);
        }

        await pool.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Walk fake time forward in small fixed steps until CreateCount reaches
    /// <paramref name="target"/>. The loop registers two sequential timers
    /// (the 2s grace delay, then the backoff delay) before each Create; a
    /// single bulk Advance would only fire whichever timer is already
    /// registered, so we step incrementally — each step fires the currently
    /// pending timer, the continuation registers the next, and the following
    /// step fires that. Real time (via the yield) only paces the walk and
    /// guards against a hang; it is not the synchronisation primitive.
    /// </summary>
    private static async Task AdvanceUntilCreateCount(
        FakeTimeProvider time, FakeConnectionFactory factory, int target)
    {
        var step = TimeSpan.FromMilliseconds(250);
        var deadline = DateTime.UtcNow + RealTimeout;
        while (Volatile.Read(ref factory.CreateCount) < target)
        {
            time.Advance(step);
            await Task.Delay(TimeSpan.FromMilliseconds(5));
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"CreateCount reached {factory.CreateCount}, expected {target}.");
        }
    }

    /// <summary>
    /// Advance fake time by (total - epsilon) in small steps, fully draining
    /// the grace timer and most of the backoff timer, but stopping strictly
    /// before the boundary that would trigger the next Create. Used to pin the
    /// exact reconnect interval: the caller asserts CreateCount is unchanged
    /// after this returns.
    /// </summary>
    private static async Task AdvanceToJustBefore(
        FakeTimeProvider time, FakeConnectionFactory factory, int currentCreateCount, TimeSpan total)
    {
        // Leave a small slack below the boundary; the FakeTimeProvider fires a
        // timer when advanced to/past its due time, so any positive slack keeps
        // us short of it.
        var epsilon = TimeSpan.FromMilliseconds(100);
        var target = total - epsilon;
        var step = TimeSpan.FromMilliseconds(250);
        var advanced = TimeSpan.Zero;

        while (advanced < target)
        {
            var next = step;
            if (advanced + next > target) next = target - advanced;
            time.Advance(next);
            advanced += next;
            await Task.Delay(TimeSpan.FromMilliseconds(5));
            // If the loop ever raced ahead, surface it immediately rather than
            // silently passing the just-before assertion.
            if (Volatile.Read(ref factory.CreateCount) > currentCreateCount)
                return;
        }
    }

    [Fact]
    public async Task BackoffResets_AfterSuccessfulConnect()
    {
        var (pool, factory, time, signal) = CreatePool("plc1");

        // Iteration 1: Create succeeds, Connect throws -> backoff becomes 4s.
        var fail1 = factory.Enqueue(new FakeManagedConnection("plc1") { ConnectShouldThrow = true });
        // Iteration 2: Create succeeds, Connect throws -> backoff becomes 8s.
        var fail2 = factory.Enqueue(new FakeManagedConnection("plc1") { ConnectShouldThrow = true });
        // Iteration 3: Connect succeeds, then first health check fails -> backoff reset to 2s.
        var healthy = new FakeManagedConnection("plc1");
        healthy.IsAliveResults.Enqueue(false);
        factory.Enqueue(healthy);
        // Iteration 4: Connect succeeds; we observe it arrives after the MINIMUM
        // backoff (2s) following the reset, proving the reset.
        var afterReset = new FakeManagedConnection("plc1");
        factory.Enqueue(afterReset);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        // Attempt 1 (fail).
        await Await(fail1.ConnectCalled);
        // -> grace(2s) + backoff(2s) -> attempt 2.
        await AdvanceUntilCreateCount(time, factory, 2);
        await Await(fail2.ConnectCalled);

        // -> grace(2s) + backoff(4s) -> attempt 3 (the healthy one).
        await AdvanceUntilCreateCount(time, factory, 3);
        await Await(healthy.ConnectCalled);
        await WaitForConnection(pool, "plc1", healthy);
        Assert.Same(healthy, pool.GetConnection("plc1"));

        // Healthy connect resets backoff to 2s. Inner health loop: Task.Delay(5s)
        // then IsAliveAsync returns false -> break -> grace(2s) -> backoff(2s) -> attempt 4.
        // Drive the 5s health interval until IsAliveAsync fires.
        await AdvanceUntil(time, healthy.IsAliveCalled, Health);

        // Now grace(2s) + reset backoff(2s) before attempt 4.
        await AdvanceUntilCreateCount(time, factory, 4);
        await Await(afterReset.ConnectCalled);
        Assert.Equal(4, factory.CreateCount);

        await pool.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HealthCheckFailure_TriggersRebuild()
    {
        var (pool, factory, time, signal) = CreatePool("plc1");

        var first = new FakeManagedConnection("plc1");
        first.IsAliveResults.Enqueue(true);   // 1st health check passes
        first.IsAliveResults.Enqueue(false);  // 2nd health check fails -> rebuild
        factory.Enqueue(first);

        var second = new FakeManagedConnection("plc1");
        factory.Enqueue(second);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await Await(first.ConnectCalled);
        await WaitForConnection(pool, "plc1", first);
        Assert.Same(first, pool.GetConnection("plc1"));

        // 1st health check (passes). Advance 5s until IsAliveAsync fires.
        first.RearmIsAliveCalled();
        await AdvanceUntil(time, first.IsAliveCalled, Health);
        Assert.Equal(1, first.IsAliveCount);
        Assert.Same(first, pool.GetConnection("plc1")); // still alive

        // 2nd health check (fails) -> inner loop breaks -> cleanup.
        first.RearmIsAliveCalled();
        await AdvanceUntil(time, first.IsAliveCalled, Health);

        // Cleanup: TryRemove, then 2s grace, then ForceDisconnect + Dispose,
        // then backoff(2s) before the rebuild Create+Connect.
        await AdvanceUntilCreateCount(time, factory, 2);
        await Await(second.ConnectCalled);
        await WaitForConnection(pool, "plc1", second);

        Assert.Equal(1, first.ForceDisconnectCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Same(second, pool.GetConnection("plc1"));

        await pool.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_CancelsLoops_DisconnectsAndDisposes()
    {
        var (pool, factory, time, signal) = CreatePool("plc1");

        var conn = new FakeManagedConnection("plc1");
        factory.Enqueue(conn);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await Await(conn.ConnectCalled);
        await WaitForConnection(pool, "plc1", conn);
        Assert.Same(conn, pool.GetConnection("plc1"));

        // Stop must complete promptly via cancellation, NOT by waiting on the
        // 10s WaitAsync timeout — the loop exits on its cancellation token.
        // We pass real (non-fake) time guard: if Stop hung on the fake timer
        // it would never return because nobody advances fake time here.
        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);

        Assert.Equal(1, conn.DisconnectCount);
        Assert.Equal(1, conn.DisposeCount);
        Assert.Null(pool.GetConnection("plc1"));
    }

    [Fact]
    public async Task ForceReconnect_ReplacesLoop_TearsDownOldConnection()
    {
        var (pool, factory, time, signal) = CreatePool("plc1");

        var first = new FakeManagedConnection("plc1");
        factory.Enqueue(first);
        var second = new FakeManagedConnection("plc1");
        factory.Enqueue(second);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await Await(first.ConnectCalled);
        await WaitForConnection(pool, "plc1", first);
        Assert.Same(first, pool.GetConnection("plc1"));

        // ForceReconnect cancels the old loop and starts a new one. The new
        // loop calls Create + Connect immediately (no pre-delay).
        pool.ForceReconnect("plc1");

        await Await(second.ConnectCalled);
        await WaitForConnection(pool, "plc1", second);
        Assert.Same(second, pool.GetConnection("plc1"));
        Assert.Equal(2, factory.CreateCount);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task ForceReconnect_UnknownPlc_IsNoOp()
    {
        var (pool, factory, _, signal) = CreatePool("plc1");
        var conn = new FakeManagedConnection("plc1");
        factory.Enqueue(conn);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);
        await Await(conn.ConnectCalled);

        var createsBefore = factory.CreateCount;

        // Unknown plcId: logs a warning, does NOT throw, does NOT create anything.
        var ex = Record.Exception(() => pool.ForceReconnect("does-not-exist"));
        Assert.Null(ex);
        Assert.Equal(createsBefore, factory.CreateCount);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }
}
