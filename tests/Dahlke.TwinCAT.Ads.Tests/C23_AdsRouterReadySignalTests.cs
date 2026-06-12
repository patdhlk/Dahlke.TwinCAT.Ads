namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// TDD tests for C23: tri-state <see cref="AdsRouterReadySignal"/>.
///
/// The signal distinguishes three terminal states — Ready, Failed(reason),
/// and Cancelled — and resolves deterministically on every path. Critically,
/// a per-waiter <see cref="CancellationToken"/> firing must NOT poison the
/// shared signal: it cancels only that waiter, leaving the shared state free
/// to be set Ready/Failed/Cancelled afterwards for other waiters.
/// </summary>
public class C23_AdsRouterReadySignalTests
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(10);

    // -------------------------------------------------------------------------
    // 1. Ready: WaitAsync completes normally
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SetReady_WaitAsync_CompletesNormally()
    {
        var signal = new AdsRouterReadySignal();
        signal.SetReady();

        // Must complete without throwing.
        await signal.WaitAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task WaitAsync_BeforeSetReady_CompletesOnceReady()
    {
        var signal = new AdsRouterReadySignal();

        var waitTask = signal.WaitAsync(CancellationToken.None);
        Assert.False(waitTask.IsCompleted);

        signal.SetReady();

        await waitTask.WaitAsync(RealTimeout);
    }

    // -------------------------------------------------------------------------
    // 2. Failed: WaitAsync throws with the stored reason
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SetFailed_WaitAsync_ThrowsWithReasonAsInnerException()
    {
        var signal = new AdsRouterReadySignal();
        var reason = new InvalidOperationException("router bind failed on port 48898");
        signal.SetFailed(reason);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signal.WaitAsync(CancellationToken.None));

        // The thrown exception must carry the original reason so the pool can log it.
        Assert.Same(reason, ex.InnerException);
        Assert.Contains("router", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // 3. Cancelled (signal-level): WaitAsync throws TaskCanceledException
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SetCancelled_WaitAsync_ThrowsTaskCanceled()
    {
        var signal = new AdsRouterReadySignal();
        signal.SetCancelled();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => signal.WaitAsync(CancellationToken.None));
    }

    // -------------------------------------------------------------------------
    // 4. First terminal state wins (TrySet semantics)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SetFailedThenSetReady_StaysFailed()
    {
        var signal = new AdsRouterReadySignal();
        var reason = new InvalidOperationException("first failure wins");
        signal.SetFailed(reason);
        signal.SetReady(); // no-op: already terminal

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signal.WaitAsync(CancellationToken.None));
        Assert.Same(reason, ex.InnerException);
    }

    [Fact]
    public async Task SetReadyThenSetFailed_StaysReady()
    {
        var signal = new AdsRouterReadySignal();
        signal.SetReady();
        signal.SetFailed(new InvalidOperationException("too late"));

        // Already Ready — WaitAsync still completes normally.
        await signal.WaitAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task SetCancelledThenSetReady_StaysCancelled()
    {
        var signal = new AdsRouterReadySignal();
        signal.SetCancelled();
        signal.SetReady();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => signal.WaitAsync(CancellationToken.None));
    }

    // -------------------------------------------------------------------------
    // 5. THE poison regression: per-waiter ct cancellation cancels ONLY that
    //    waiter; a later SetReady() still completes other / new waiters.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PerWaiterCancellation_DoesNotPoisonSharedSignal()
    {
        var signal = new AdsRouterReadySignal();

        using var waiterCts = new CancellationTokenSource();

        // Waiter A waits with its own cancellable token.
        var waiterA = signal.WaitAsync(waiterCts.Token);

        // A second waiter (B) waits with no token — represents the real consumer.
        var waiterB = signal.WaitAsync(CancellationToken.None);

        // Cancel ONLY waiter A's token.
        waiterCts.Cancel();

        // Waiter A observes its own cancellation as an OCE.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiterA);

        // The shared signal must NOT be poisoned: SetReady still resolves it.
        signal.SetReady();

        // Waiter B (registered before the cancellation) completes normally.
        await waiterB.WaitAsync(RealTimeout);

        // A brand-new waiter also completes normally.
        await signal.WaitAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task PerWaiterCancellation_ThenSetFailed_NewWaiterSeesReason()
    {
        var signal = new AdsRouterReadySignal();

        using var waiterCts = new CancellationTokenSource();
        var waiterA = signal.WaitAsync(waiterCts.Token);
        waiterCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiterA);

        // After a poisoning-by-cancel attempt, the signal can still go Failed.
        var reason = new InvalidOperationException("router really failed");
        signal.SetFailed(reason);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signal.WaitAsync(CancellationToken.None));
        Assert.Same(reason, ex.InnerException);
    }

    // -------------------------------------------------------------------------
    // 6. Pre-cancelled token: immediate OCE, no poisoning
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PreCancelledToken_ThrowsImmediately_WithoutPoisoning()
    {
        var signal = new AdsRouterReadySignal();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // A pre-cancelled token fires the per-waiter cancellation immediately.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => signal.WaitAsync(cts.Token));

        // Shared state untouched: SetReady still completes a fresh waiter.
        signal.SetReady();
        await signal.WaitAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }
}
