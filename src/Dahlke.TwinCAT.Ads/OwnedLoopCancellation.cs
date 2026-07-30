namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// A cancellation signal with the loop-teardown ownership discipline built in:
/// teardown paths <see cref="RequestStop"/> (cancel-only — any number of times,
/// from any thread, never throwing, never disposing), and the OWNING loop alone
/// <see cref="OwnerRetire"/>s the signal, in its own <c>finally</c>, after it
/// has exited. Its three call sites are the pool's per-target reconnect loops,
/// the raw factory's idle sweeper, and the raw channel's shutdown signal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why cancel-only for teardown paths.</b>
/// <see cref="CancellationTokenSource.Dispose()"/> is not safe while another
/// thread is inside <see cref="CancellationTokenSource.Cancel()"/> on the same
/// source: only one caller executes the registered callbacks, a second
/// <c>Cancel</c> returns immediately WITHOUT waiting for them, and if that
/// second caller then disposes it can free the registration list while the
/// winner is still walking it — dropping a pending registration without ever
/// invoking it. That is exactly what once wedged pool shutdown: the reconnect
/// loop parks on a <c>Task.Delay</c> whose completion IS such a registration;
/// lose it and the loop never exits, and <c>StopAsync</c> waits forever. The
/// same race also surfaced as <see cref="ObjectDisposedException"/> out of
/// <c>Cancel</c> — one root cause, two symptoms.
/// </para>
/// <para>
/// <b>Why the owner disposes, and only after exit.</b> By the time the owning
/// loop's <c>finally</c> runs, the loop can no longer hold a token
/// registration — the registration that woke it has already fired — so retiring
/// there cannot race a cancel into dropping anything. Exactly one
/// <see cref="RequestStop"/> ever reaches <c>Cancel</c> (a once-flag), so there
/// is no second canceller to race the walk either.
/// </para>
/// <para>
/// <b>The one residual race, absorbed here once.</b> A loop that exits
/// ABNORMALLY (an escaping exception) retires its signal before any stop was
/// requested; the first-ever <see cref="RequestStop"/> can then reach a
/// disposed source. The primitive absorbs that
/// <see cref="ObjectDisposedException"/>: a retired signal means the loop
/// already exited, so there is provably nothing left to cancel and the stop
/// request's only meaning — "loop, exit" — is already satisfied.
/// <see cref="IsStopRequested"/> still reads <see langword="true"/>, from the
/// once-flag rather than the token, so gated work observing the signal (the raw
/// channel's restore pass) sees a consistent answer.
/// </para>
/// <para>
/// <b>A signal nobody retires is a supported shape.</b> The raw channel's
/// shutdown flag has no owning loop; it is raised at most once and deliberately
/// never retired. A source with no timer and no lingering registrations of its
/// own may live undisposed — cancel-after-dispose is the hazard, and a source
/// nobody disposes cannot be cancelled after disposal. Linked sources built
/// from <see cref="Token"/> are disposed by their creators per use, which
/// unregisters them here.
/// </para>
/// </remarks>
internal sealed class OwnedLoopCancellation
{
    private readonly CancellationTokenSource _source = new();
    private int _stopRequested;
    private int _retired;

    public OwnedLoopCancellation() => Token = _source.Token;

    /// <summary>
    /// The token the owning loop (and anything gated on it) observes. Remains
    /// readable after <see cref="OwnerRetire"/>.
    /// </summary>
    public CancellationToken Token { get; }

    /// <summary>
    /// Whether a stop has been requested. Read from the once-flag, not the
    /// token, so it is <see langword="true"/> after a stop request even when the
    /// request arrived too late to cancel an already-retired signal.
    /// </summary>
    public bool IsStopRequested => Volatile.Read(ref _stopRequested) != 0;

    /// <summary>
    /// Requests the owning loop to stop. Cancel-only: never disposes, never
    /// throws, callable from any teardown path any number of times.
    /// </summary>
    public void RequestStop()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
            return; // exactly one requester ever reaches Cancel

        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The owner retired after an abnormal exit before any stop was
            // requested. Provably benign — see the class remarks.
        }
    }

    /// <summary>
    /// Retires the signal. To be called ONLY by the owning loop, in its own
    /// <c>finally</c>, after it has exited — never by a teardown path.
    /// Idempotent. A signal with no owning loop is simply never retired.
    /// </summary>
    public void OwnerRetire()
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0)
            return;

        _source.Dispose();
    }
}
