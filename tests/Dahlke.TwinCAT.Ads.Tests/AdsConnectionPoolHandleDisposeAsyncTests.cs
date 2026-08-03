using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Pins two things about <see cref="AdsConnectionPoolHandle.DisposeAsync"/> that no other
/// test file catches: that the hosted-service stop loop actually runs in the exact reverse
/// of start order (<see cref="AdsConnectionPoolBuilderTests.DisposeAsync_StopsThePool_AndIsIdempotent"/>
/// would pass even with that loop deleted, since disposing the provider disconnects the pool
/// on its own), and the <see cref="AdsConnectionPoolBuilder.UseShutdownTimeout"/> knob that
/// bounds it.
/// </summary>
public class AdsConnectionPoolHandleDisposeAsyncTests
{
    /// <summary>Appends its identity to a shared log on both start and stop.</summary>
    private sealed class OrderRecordingHostedService(string name, List<string> log) : IHostedService
    {
        public Task StartAsync(CancellationToken ct)
        {
            log.Add($"{name}:start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            log.Add($"{name}:stop");
            return Task.CompletedTask;
        }
    }

    /// <summary>Records whether it was started and stopped, ignoring the token it is given.</summary>
    private sealed class ProbeHostedService : IHostedService
    {
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken ct) { Started = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { Stopped = true; return Task.CompletedTask; }
    }

    /// <summary>
    /// Blocks in <see cref="StopAsync"/> until its token is cancelled, then throws — a
    /// well-behaved (cooperative) hosted service, and the only kind a shutdown timeout can
    /// actually bound. A service that ignored the token entirely would hang this test
    /// exactly as it hangs <see cref="AdsConnectionPoolHandle.DisposeAsync"/> for real; see
    /// that method's remarks for why a shared, cooperative token is what gets passed rather
    /// than something that could forcibly abandon the call.
    /// </summary>
    private sealed class BlockingUntilCancelledHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    /// <summary>Disposed by the provider; proves the provider itself was disposed.</summary>
    private sealed class DisposeProbe : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Exists purely to be constructed BY THE CONTAINER, so that resolving it also resolves
    /// — and so disposes — the <see cref="DisposeProbe"/> it depends on. See the identical
    /// pattern (and its full rationale) in <see cref="AdsConnectionPoolBuilderParityTests"/>.
    /// </summary>
    private sealed class DisposeProbeOwner : IHostedService
    {
        public DisposeProbeOwner(DisposeProbe probe) { }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static TimeSpan GetPlumbedShutdownTimeout(AdsConnectionPoolHandle pool)
    {
        var field = typeof(AdsConnectionPoolHandle)
            .GetField("_shutdownTimeout", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (TimeSpan)field!.GetValue(pool)!;
    }

    [Fact]
    public async Task DisposeAsync_StopsInExactReverseOfStartOrder()
    {
        var events = new List<string>();

        var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .ConfigureServices(s =>
            {
                s.AddSingleton<IHostedService>(new OrderRecordingHostedService("P1", events));
                s.AddSingleton<IHostedService>(new OrderRecordingHostedService("P2", events));
            })
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC")
            .BuildAndStartAsync();

        // Caller-registered hosted services are stable-moved to the end of the collection
        // (see AdsConnectionPoolBuilder.ConfigureServices's remarks), so both probes start
        // after router/pool/raw-channels and in their own registration order.
        Assert.Equal(["P1:start", "P2:start"], events);

        await pool.DisposeAsync();

        // If the stop loop were deleted, only the two ":start" entries above would exist.
        // If the loop iterated forwards instead of backwards, the tail would read
        // "P1:stop", "P2:stop" instead. Only the true reverse-order loop produces this.
        Assert.Equal(["P1:start", "P2:start", "P2:stop", "P1:stop"], events);
    }

    [Fact]
    public async Task DisposeAsync_NeverCallsStopAsync_OnAServiceThatNeverStarted()
    {
        // ExplodingOnStart throws out of StartAsync, so BuildAndStartAsync's own unwind
        // (not DisposeAsync — the pool is never handed to the caller) must stop what DID
        // start and must NOT call StopAsync on the service that never started. The existing
        // FailedStart_StopsWhatStarted_AndDisposesTheProvider in
        // AdsConnectionPoolBuilderParityTests proves the unwind reaches started services;
        // this complements it from the other side.
        var probe = new ProbeHostedService();
        var exploder = new ExplodingOnStartHostedService();

        var builder = AdsConnectionPoolBuilder.CreateSimulation()
            .ConfigureServices(s =>
            {
                s.AddSingleton<IHostedService>(probe);
                s.AddSingleton<IHostedService>(exploder);
            })
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC");

        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAndStartAsync());

        Assert.True(probe.Started);
        Assert.True(probe.Stopped);
        Assert.False(exploder.StopWasCalled);
    }

    /// <summary>Throws from StartAsync, so it is never added to the "started" list.</summary>
    private sealed class ExplodingOnStartHostedService : IHostedService
    {
        public bool StopWasCalled { get; private set; }

        public Task StartAsync(CancellationToken ct) =>
            throw new InvalidOperationException("start failed");

        // If this were ever invoked the unwind would be stopping a service that never
        // started — recorded here so a future change to this test can assert it directly.
        public Task StopAsync(CancellationToken ct) { StopWasCalled = true; return Task.CompletedTask; }
    }

    [Fact]
    public async Task ShutdownTimeout_DefaultsTo30Seconds_WhenNeverConfigured()
    {
        await using var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC")
            .BuildAndStartAsync();

        Assert.Equal(TimeSpan.FromSeconds(30), GetPlumbedShutdownTimeout(pool));
    }

    [Fact]
    public async Task UseShutdownTimeout_PlumbsInfiniteTimeSpan()
    {
        await using var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .UseShutdownTimeout(Timeout.InfiniteTimeSpan)
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC")
            .BuildAndStartAsync();

        Assert.Equal(Timeout.InfiniteTimeSpan, GetPlumbedShutdownTimeout(pool));
    }

    [Fact]
    public void UseShutdownTimeout_NegativeValue_ThrowsArgumentOutOfRangeException()
    {
        var builder = AdsConnectionPoolBuilder.Create();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.UseShutdownTimeout(TimeSpan.FromSeconds(-1)));

        Assert.Equal("timeout", ex.ParamName);
    }

    [Fact]
    public async Task DisposeAsync_AbandonsAHangingService_AndStillCompletesAndDisposesTheProvider()
    {
        var disposeProbe = new DisposeProbe();

        var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .UseShutdownTimeout(TimeSpan.FromMilliseconds(200))
            .ConfigureServices(s =>
            {
                s.AddSingleton<DisposeProbe>(_ => disposeProbe);
                // Forces DisposeProbeOwner to be constructed, which resolves DisposeProbe —
                // see DisposeProbeOwner's doc comment for why neither alone is enough.
                s.AddSingleton<IHostedService, DisposeProbeOwner>();
                s.AddSingleton<IHostedService, BlockingUntilCancelledHostedService>();
            })
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC")
            .BuildAndStartAsync();

        var sw = Stopwatch.StartNew();
        // WaitAsync is the hard bound: if a future change stops passing the shared token
        // through to StopAsync, this fails the test outright instead of wedging the run.
        await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        sw.Stop();

        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(5),
            $"DisposeAsync took {sw.Elapsed}; expected it to be bounded by the 200ms shutdown timeout, not to hang.");
        Assert.True(disposeProbe.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_SharesOneTimeoutBudget_AcrossMultipleHangingServices()
    {
        // Two independently-hanging services under one 400ms budget. A correct
        // implementation spends ~400ms total (the loop moves to the second service with an
        // already-cancelled token, which returns immediately) plus whatever the pool itself
        // takes to stop. A per-service timeout (the bug this test catches) would spend
        // ~800ms — two full budgets back to back. 700ms sits between the two with margin on
        // both sides for CI jitter.
        var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .UseShutdownTimeout(TimeSpan.FromMilliseconds(400))
            .ConfigureServices(s =>
            {
                s.AddSingleton<IHostedService, BlockingUntilCancelledHostedService>();
                s.AddSingleton<IHostedService, BlockingUntilCancelledHostedService>();
            })
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC")
            .BuildAndStartAsync();

        var sw = Stopwatch.StartNew();
        await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        sw.Stop();

        Assert.True(
            sw.Elapsed < TimeSpan.FromMilliseconds(700),
            $"DisposeAsync took {sw.Elapsed}; a shared timeout budget must not multiply per hanging service.");
    }

    [Fact]
    public async Task DisposeAsync_ATimedOutService_DoesNotPreventEarlierRegisteredServicesFromStopping()
    {
        var probe = new ProbeHostedService();

        var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .UseShutdownTimeout(TimeSpan.FromMilliseconds(200))
            .ConfigureServices(s =>
            {
                // Registered — and so started — BEFORE the blocking service, which makes it
                // stop AFTER the blocking service has already exhausted the shared budget:
                // the loop runs in reverse, so the blocking service (started last) times out
                // first, and this probe (started first) is stopped next, with an
                // already-cancelled token.
                s.AddSingleton<IHostedService>(probe);
                s.AddSingleton<IHostedService, BlockingUntilCancelledHostedService>();
            })
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC")
            .BuildAndStartAsync();

        Assert.True(probe.Started);

        await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(probe.Stopped);
    }
}
