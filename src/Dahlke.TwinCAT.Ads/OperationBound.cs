namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// One operation's cancellation bound: a linked source (caller token + timeout),
/// optionally with the separate timer source it was linked from. Disposal
/// CANCELS the linked source before releasing it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cancel-on-dispose is the point of this type — do not "simplify" it to
/// two <c>using</c>s.</b> Disposing a <see cref="CancellationTokenSource"/> does
/// not fire it, and the token this bound produced has been handed to third-party
/// code: Beckhoff's <c>AdsClientServer.RequestAsync</c> races every request
/// against <c>Task.Delay(Timeout, token)</c> and, when the request wins, simply
/// ABANDONS the delay — never cancels it. An abandoned delay keeps its
/// runtime timer (<c>TimerQueueTimer</c>) armed until its own due time,
/// and the timer roots the delay promise, this bound's linked source and both
/// registration nodes for that long. Under <see cref="AdsRawChannel"/>'s
/// one-hour client backstop that was ~30 armed timers per second accumulating
/// for an hour each — ~100&#160;MB of reachable heap at steady state, found as
/// memory growth that froze a 1&#160;GB embedded host in about two hours (2026-08-27).
/// Cancelling here completes the delay the moment the owning scope exits, so
/// the whole graph dies with the operation instead of outliving it.
/// </para>
/// <para>
/// Cancelling a token whose operation already completed is safe by construction:
/// the token was created for that one operation and nothing else observes it.
/// On failure paths the cancel doubles as the reap — an operation abandoned by
/// timeout or retry is completed on the Beckhoff side (error 1878) instead of
/// staying outstanding against the client backstop.
/// </para>
/// </remarks>
internal readonly struct OperationBound : IDisposable
{
    private readonly CancellationTokenSource _linked;
    private readonly CancellationTokenSource? _timer;

    public OperationBound(CancellationTokenSource linked, CancellationTokenSource? timer = null)
    {
        _linked = linked;
        _timer = timer;
    }

    public CancellationToken Token => _linked.Token;

    public void Dispose()
    {
        _linked.Cancel();
        _linked.Dispose();
        _timer?.Dispose();
    }
}
