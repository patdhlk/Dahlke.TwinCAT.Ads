namespace Dahlke;

/// <summary>
/// The net6+ conveniences the suites use throughout, absent from .NET Framework 4.8: the
/// single-argument <c>Task.WaitAsync</c> overloads and
/// <c>CancellationTokenSource.CancelAsync</c>. Compiled ONLY into the net48 test leg — see
/// tests/Directory.Build.props — so on every other framework the BCL's own members bind and this
/// file does not exist. Declared in the <c>Dahlke</c> namespace so every test project finds the
/// extensions through the enclosing-namespace walk, with no per-file using.
/// </summary>
/// <remarks>
/// Semantics mirror the BCL members they stand in for: a timed-out wait faults with
/// <see cref="TimeoutException"/>, a cancelled wait with an <see cref="OperationCanceledException"/>
/// carrying the caller's token, and neither observes or abandons the underlying task's own
/// exception — awaiting the source task later still surfaces it.
/// </remarks>
internal static class Net48TestCompat
{
    public static Task WaitAsync(this Task task, CancellationToken cancellationToken) =>
        task.WaitAsync(Timeout.InfiniteTimeSpan, cancellationToken);

    public static Task<TResult> WaitAsync<TResult>(this Task<TResult> task, CancellationToken cancellationToken) =>
        task.WaitAsync(Timeout.InfiniteTimeSpan, cancellationToken);

    public static Task WaitAsync(this Task task, TimeSpan timeout) =>
        task.WaitAsync(timeout, CancellationToken.None);

    public static Task<TResult> WaitAsync<TResult>(this Task<TResult> task, TimeSpan timeout) =>
        task.WaitAsync(timeout, CancellationToken.None);

    public static async Task WaitAsync(this Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completed = await Task.WhenAny(task, Task.Delay(timeout, delayCts.Token)).ConfigureAwait(false);
        if (completed == task)
        {
            delayCts.Cancel();
            await task.ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException();
    }

    public static async Task<TResult> WaitAsync<TResult>(this Task<TResult> task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completed = await Task.WhenAny(task, Task.Delay(timeout, delayCts.Token)).ConfigureAwait(false);
        if (completed == task)
        {
            delayCts.Cancel();
            return await task.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException();
    }

    public static Task CancelAsync(this CancellationTokenSource source)
    {
        // net8's CancelAsync differs only in not blocking the caller on synchronous callback
        // execution; every test that calls this awaits it immediately, so running the callbacks
        // inline first is observably the same.
        source.Cancel();
        return Task.CompletedTask;
    }
}
