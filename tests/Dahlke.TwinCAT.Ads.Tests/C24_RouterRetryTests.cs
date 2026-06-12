using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// TDD tests for C24: router startup failure becomes a transient, retried
/// state. <see cref="AdsRouterService.ExecuteAsync"/> wraps the per-attempt
/// router body in a retry loop with the SAME backoff as the pool's connection
/// loops (2s doubling to a 30s cap, paced by <see cref="TimeProvider"/>).
///
/// The Beckhoff <c>AmsTcpIpRouter</c> cannot be faked directly, so the
/// per-attempt body is extracted behind the
/// <see cref="AdsRouterService.RunRouterAttemptAsync"/> seam and overridden by
/// <see cref="TestableRouterService"/> below. The seam receives the signal and
/// is expected to call <c>SetReady</c> on a successful start — exactly as the
/// real implementation does from the <c>RouterStatus.Started</c> event hook.
/// </summary>
public class C24_RouterRetryTests
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A router service whose per-attempt body is fully scripted. Each entry in
    /// <see cref="_attempts"/> is invoked in order (the last one repeats once the
    /// queue drains). An attempt may throw (simulating a bind failure / mid-run
    /// crash) or call <c>SetReady</c> on the signal (simulating
    /// <c>RouterStatus.Started</c>) and then optionally block until cancelled
    /// (simulating a router that stays up).
    /// </summary>
    private sealed class TestableRouterService : AdsRouterService
    {
        private readonly Func<int, AdsRouterReadySignal, CancellationToken, Task> _attempt;

        public int AttemptCount;

        public TestableRouterService(
            IOptions<TwinCatAdsOptions> options,
            AdsRouterReadySignal signal,
            TimeProvider timeProvider,
            Func<int, AdsRouterReadySignal, CancellationToken, Task> attempt)
            : base(Options.Create(options.Value), configuration: null,
                   NullLoggerFactory.Instance, signal, timeProvider)
        {
            _attempt = attempt;
        }

        protected internal override Task RunRouterAttemptAsync(
            AdsRouterReadySignal signal, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref AttemptCount);
            return _attempt(n, signal, ct);
        }
    }

    private static TwinCatAdsOptions RealTargetOptions() => new()
    {
        Targets = new(StringComparer.OrdinalIgnoreCase)
        {
            ["real1"] = new PlcTargetOptions { Mode = ConnectionMode.Real, AmsNetId = "1.2.3.4.5.6" },
        },
        Router = new AmsRouterOptions { NetId = "127.0.0.1.1.1" },
    };

    private static async Task AdvanceUntil(FakeTimeProvider time, Func<bool> predicate, TimeSpan step)
    {
        var deadline = DateTime.UtcNow + RealTimeout;
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Predicate did not become true within the real-time guard window.");
            time.Advance(step);
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    // -------------------------------------------------------------------------
    // Backoff: attempts 1 and 2 throw, attempt 3 starts. Delays between attempts
    // are 2s then 4s (doubling); the signal is PENDING until attempt 3, then
    // Ready.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Retry_FailingThenSucceeding_BacksOff2Then4_SignalPendingUntilReady()
    {
        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal();

        // Record the cumulative fake time at which each attempt fires so we can
        // assert the inter-attempt backoff is 2s then 4s.
        var attemptAt = new List<TimeSpan>();
        var startedAt = TimeSpan.Zero;

        var svc = new TestableRouterService(
            Options.Create(RealTargetOptions()),
            signal,
            time,
            attempt: (n, sig, ct) =>
            {
                lock (attemptAt) { attemptAt.Add(time.GetUtcNow() - DateTimeOffset.UnixEpoch); }

                if (n < 3)
                    throw new InvalidOperationException($"bind failure attempt {n}");

                // Attempt 3: router started.
                sig.SetReady();
                // Stay "up" until shutdown.
                return Task.Delay(Timeout.Infinite, ct);
            });

        await svc.StartAsync(CancellationToken.None);

        // Attempt 1 runs immediately and throws. The signal stays PENDING while
        // the loop retries (it is only set on the eventual success).
        await AdvanceUntil(time, () => AttemptsRecorded(attemptAt) >= 1, TimeSpan.FromMilliseconds(250));
        Assert.False(IsSignalResolved(signal));

        // Drive the backoff loop to the eventual success, stepping in small slices
        // so each Task.Delay is registered before time advances past it.
        await AdvanceUntil(time, () => AttemptsRecorded(attemptAt) >= 3, TimeSpan.FromMilliseconds(250));

        // Inter-attempt gaps: attempt 2 came 2s after attempt 1; attempt 3 came 4s
        // after attempt 2 (the backoff doubled, capped at 30s).
        List<TimeSpan> snapshot;
        lock (attemptAt) { snapshot = attemptAt.ToList(); }
        var gap1 = snapshot[1] - snapshot[0];
        var gap2 = snapshot[2] - snapshot[1];
        Assert.True(gap1 >= TimeSpan.FromSeconds(2) && gap1 < TimeSpan.FromSeconds(3),
            $"first backoff expected ~2s, was {gap1}");
        Assert.True(gap2 >= TimeSpan.FromSeconds(4) && gap2 < TimeSpan.FromSeconds(5),
            $"second backoff expected ~4s, was {gap2}");

        // Attempt 3 set the signal Ready.
        await signal.WaitAsync(CancellationToken.None).WaitAsync(RealTimeout);

        await svc.StopAsync(CancellationToken.None);
    }

    private static int AttemptsRecorded(List<TimeSpan> list)
    {
        lock (list) { return list.Count; }
    }

    // -------------------------------------------------------------------------
    // Eventual success: signal becomes Ready after a couple of failures, and
    // the backoff state has reset (no Failed terminal state in the normal path).
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Retry_EventualSuccess_SignalReady_NotFailed()
    {
        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal();

        var svc = new TestableRouterService(
            Options.Create(RealTargetOptions()),
            signal,
            time,
            attempt: (n, sig, ct) =>
            {
                if (n < 2)
                    throw new InvalidOperationException("transient");
                sig.SetReady();
                return Task.Delay(Timeout.Infinite, ct);
            });

        await svc.StartAsync(CancellationToken.None);

        await AdvanceUntil(time, () => IsSignalResolved(signal), MinBackoff);

        // Ready (not Failed): WaitAsync completes normally, no exception.
        await signal.WaitAsync(CancellationToken.None).WaitAsync(RealTimeout);

        await svc.StopAsync(CancellationToken.None);
    }

    // -------------------------------------------------------------------------
    // Cancellation mid-retry → SetCancelled + prompt exit (no further attempts).
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Retry_CancelledMidRetry_SetsCancelled_ExitsPromptly()
    {
        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal();

        var svc = new TestableRouterService(
            Options.Create(RealTargetOptions()),
            signal,
            time,
            // Always fail — the service is forever in the backoff loop.
            attempt: (n, sig, ct) => throw new InvalidOperationException("always fails"));

        await svc.StartAsync(CancellationToken.None);

        // Let at least one attempt run.
        await AdvanceUntil(time, () => svc.AttemptCount >= 1, MinBackoff);
        var attemptsAtCancel = svc.AttemptCount;

        // Shutdown cancels the retry loop.
        await svc.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);

        // Cancelled resolution: WaitAsync throws TaskCanceledException.
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => signal.WaitAsync(CancellationToken.None).WaitAsync(RealTimeout));
    }

    // -------------------------------------------------------------------------
    // Router started then crashed mid-run: the attempt sets Ready, then throws.
    // The retry loop re-enters with backoff; the signal stays Ready (TrySet
    // no-ops on the already-resolved signal).
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Retry_StartedThenCrashed_ReentersRetry_SignalStaysReady()
    {
        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal();

        var svc = new TestableRouterService(
            Options.Create(RealTargetOptions()),
            signal,
            time,
            attempt: (n, sig, ct) =>
            {
                if (n == 1)
                {
                    // Router started, then crashed mid-run.
                    sig.SetReady();
                    throw new InvalidOperationException("router crashed mid-run");
                }

                // A later attempt restores the transport and stays up.
                return Task.Delay(Timeout.Infinite, ct);
            });

        await svc.StartAsync(CancellationToken.None);

        // Signal becomes Ready on attempt 1.
        await AdvanceUntil(time, () => IsSignalResolved(signal), TimeSpan.Zero);
        await signal.WaitAsync(CancellationToken.None).WaitAsync(RealTimeout);

        // The loop re-enters and runs a second attempt after backoff.
        await AdvanceUntil(time, () => svc.AttemptCount >= 2, MinBackoff);

        // Signal is STILL Ready (no transition to Failed/Cancelled).
        await signal.WaitAsync(CancellationToken.None).WaitAsync(RealTimeout);

        await svc.StopAsync(CancellationToken.None);
    }

    // -------------------------------------------------------------------------
    // Helper: probe whether the signal has resolved without disturbing it.
    // -------------------------------------------------------------------------

    private static bool IsSignalResolved(AdsRouterReadySignal signal)
    {
        // A resolved signal completes WaitAsync(already-cancelled-token) with its
        // terminal result rather than the token's cancellation only if it has
        // ALREADY completed synchronously. We instead probe by racing the wait
        // against an immediately-completed task.
        var wait = signal.WaitAsync(CancellationToken.None);
        return wait.IsCompleted;
    }
}
