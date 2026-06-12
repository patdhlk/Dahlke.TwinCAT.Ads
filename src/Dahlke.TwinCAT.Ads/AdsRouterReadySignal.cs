namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// One-shot, tri-state readiness signal published by
/// <see cref="AdsRouterService"/> and awaited by <see cref="AdsConnectionPool"/>.
/// </summary>
/// <remarks>
/// <para>
/// The signal resolves to exactly one of three terminal states, and the first
/// caller to set a state wins (later <c>Set*</c> calls are silent no-ops):
/// </para>
/// <list type="bullet">
///   <item>
///     <b>Ready</b> — the router started (or was deliberately skipped). Awaiters
///     return normally.
///   </item>
///   <item>
///     <b>Failed(reason)</b> — the router faulted. Awaiters throw
///     <see cref="InvalidOperationException"/> whose
///     <see cref="Exception.InnerException"/> is the captured reason, so the
///     pool can log <em>why</em> the router is unavailable.
///   </item>
///   <item>
///     <b>Cancelled</b> — the host is shutting the router down before it ever
///     became ready. Awaiters throw <see cref="TaskCanceledException"/>.
///   </item>
/// </list>
/// <para>
/// Failure and cancellation are kept distinct on purpose: conflating them (an
/// earlier design called <c>TrySetCanceled</c> for both) meant the pool could
/// not report the real reason a router was unavailable.
/// </para>
/// <para>
/// <b>Per-waiter cancellation never poisons the shared state.</b>
/// <see cref="WaitAsync"/> links the caller's <see cref="CancellationToken"/>
/// through <see cref="Task.WaitAsync(CancellationToken)"/> rather than
/// registering a callback that completes the shared
/// <see cref="TaskCompletionSource{TResult}"/>. A waiter whose token fires (or
/// is already cancelled) observes its own <see cref="OperationCanceledException"/>
/// while the shared signal stays pending — a subsequent <see cref="SetReady"/>,
/// <see cref="SetFailed"/>, or <see cref="SetCancelled"/> still resolves every
/// other (and future) waiter.
/// </para>
/// </remarks>
internal sealed class AdsRouterReadySignal
{
    private enum RouterReadyState
    {
        Ready,
        Failed,
        Cancelled,
    }

    private readonly record struct RouterReadyResult(RouterReadyState State, Exception? Reason);

    private readonly TaskCompletionSource<RouterReadyResult> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Awaits the signal's terminal state.
    /// </summary>
    /// <param name="ct">
    /// Per-waiter cancellation token. If it fires (or is already cancelled) this
    /// call throws <see cref="OperationCanceledException"/> for this waiter only;
    /// the shared signal is left untouched.
    /// </param>
    /// <returns>
    /// A task that completes when the signal is resolved:
    /// <list type="bullet">
    ///   <item>Ready → completes normally.</item>
    ///   <item>
    ///     Failed → throws <see cref="InvalidOperationException"/> wrapping the
    ///     captured reason.
    ///   </item>
    ///   <item>Cancelled → throws <see cref="TaskCanceledException"/>.</item>
    /// </list>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was signalled before the shared signal resolved.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The signal resolved to Failed; the original reason is the inner exception.
    /// </exception>
    /// <exception cref="TaskCanceledException">
    /// The signal resolved to Cancelled.
    /// </exception>
    public async Task WaitAsync(CancellationToken ct)
    {
        // WaitAsync wraps the shared task with a per-waiter cancellation that
        // creates its OWN linked task — it never completes _tcs, so one waiter's
        // cancelled token cannot poison the signal for others.
        var result = await _tcs.Task.WaitAsync(ct).ConfigureAwait(false);

        switch (result.State)
        {
            case RouterReadyState.Ready:
                return;
            case RouterReadyState.Failed:
                throw new InvalidOperationException(
                    "ADS router failed to start", result.Reason);
            case RouterReadyState.Cancelled:
            default:
                throw new TaskCanceledException("ADS router start was cancelled.");
        }
    }

    /// <summary>
    /// Resolves the signal to <b>Ready</b>. No-op if already resolved.
    /// </summary>
    public void SetReady() =>
        _tcs.TrySetResult(new RouterReadyResult(RouterReadyState.Ready, null));

    /// <summary>
    /// Resolves the signal to <b>Failed</b>, capturing <paramref name="reason"/>
    /// so awaiters (and the pool's log) can report why the router is unavailable.
    /// No-op if already resolved.
    /// </summary>
    /// <param name="reason">The exception that caused the router to fail.</param>
    public void SetFailed(Exception reason) =>
        _tcs.TrySetResult(new RouterReadyResult(RouterReadyState.Failed, reason));

    /// <summary>
    /// Resolves the signal to <b>Cancelled</b> (host shutting the router down
    /// before it became ready). No-op if already resolved.
    /// </summary>
    public void SetCancelled() =>
        _tcs.TrySetResult(new RouterReadyResult(RouterReadyState.Cancelled, null));
}
