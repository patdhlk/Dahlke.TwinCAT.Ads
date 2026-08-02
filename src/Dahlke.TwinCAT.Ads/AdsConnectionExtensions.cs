namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Extension methods over <see cref="IAdsConnection"/>.
/// </summary>
public static class AdsConnectionExtensions
{
    /// <summary>
    /// Waits until <paramref name="connection"/> reports connected, or until
    /// <paramref name="timeout"/> elapses.
    /// </summary>
    /// <param name="connection">The connection to observe.</param>
    /// <param name="timeout">
    /// How long to wait. <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> waits
    /// indefinitely; <see cref="TimeSpan.Zero"/> polls once without waiting.
    /// </param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>
    /// <see langword="true"/> once the connection is connected; <see langword="false"/>
    /// if <paramref name="timeout"/> elapsed first.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> is negative and is not
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <remarks>
    /// <para>
    /// A timeout is reported as a RETURN VALUE rather than an exception, so the common
    /// call reads as a check — <c>if (!await conn.WaitForConnectedAsync(…))</c> — instead
    /// of routing normal startup timing through a <c>catch</c>.
    /// </para>
    /// <para>
    /// The point of this method is startup timing. A SIMULATED target is connected the
    /// moment the pool starts, so waiting on one returns immediately. A REAL target's
    /// connection loop is deferred until the embedded ADS router becomes ready, which is
    /// asynchronous and retried with backoff — so a caller that reads immediately after
    /// starting a pool gets <see cref="AdsConnectionUnavailableException"/> from the
    /// facade's own wait-then-throw rather than a diagnosis.
    /// </para>
    /// </remarks>
    public static async Task<bool> WaitForConnectedAsync(
        this IAdsConnection connection,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(
                nameof(timeout), timeout, "The timeout must not be negative.");

        ct.ThrowIfCancellationRequested();

        if (connection.IsConnected)
            return true;

        var connected = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStateChanged(object? sender, ConnectionStateChangedEventArgs e)
        {
            if (e.State == ConnectionState.Connected)
                connected.TrySetResult(true);
        }

        connection.ConnectionStateChanged += OnStateChanged;
        try
        {
            // Re-check AFTER subscribing. A target that connected in the window between
            // the check above and this subscription raised its event with no listener,
            // so without this the caller would wait out the full timeout for a
            // transition that has already happened.
            if (connection.IsConnected)
                return true;

            // The delay is cancelled when the connection wins, so a long timeout does
            // not leave a timer alive for its full duration after the method returns.
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(timeout, delayCts.Token);

            var completed = await Task.WhenAny(connected.Task, delay).ConfigureAwait(false);
            if (completed == connected.Task)
            {
                delayCts.Cancel();
                return true;
            }

            // The delay finished. Either the timeout genuinely elapsed, or the caller's
            // token cancelled it — distinguish, because those mean different things.
            ct.ThrowIfCancellationRequested();
            return false;
        }
        finally
        {
            connection.ConnectionStateChanged -= OnStateChanged;
        }
    }
}
