using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TwinCAT.Ads;
using Dahlke.TwinCAT.Ads.Tests.Fakes;

namespace Dahlke.TwinCAT.Ads.Tests;

public class AdsRawChannelRetryTests
{
    /// <summary>
    /// Hands out a NEW transport each time, recording them, so a test can prove
    /// retry re-created rather than reused.
    /// </summary>
    /// <remarks>
    /// The stall is armed HERE, when the transport is handed out, rather than by
    /// the test after starting the operation: the channel runs an attempt
    /// synchronously up to its first await, so a flag set afterwards arrives too
    /// late and the transport answers normally instead of stalling.
    /// </remarks>
    private sealed class TransportSource
    {
        private readonly List<(uint Ig, uint Io, byte[] Data)> _seeds = [];
        private TaskCompletionSource<InMemoryManagedRawConnection> _next =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<InMemoryManagedRawConnection> Created { get; } = [];

        /// <summary>How many of the transports handed out stall their first operation.</summary>
        public int StallFirst { get; set; }

        /// <summary>
        /// Completes with the NEXT transport handed out. Capture it BEFORE the
        /// retry runs, then await it to know the retry's transport exists.
        /// </summary>
        public Task<InMemoryManagedRawConnection> NextTransport => Volatile.Read(ref _next).Task;

        /// <summary>Data every transport is born with — a device keeps its data across a reconnect.</summary>
        public void Seed(uint ig, uint io, byte[] data) => _seeds.Add((ig, io, data));

        public IManagedRawConnection Create(string netId, int port)
        {
            var transport = new InMemoryManagedRawConnection();

            foreach (var (ig, io, data) in _seeds)
                transport.Seed(ig, io, data);

            if (Created.Count < StallFirst)
                transport.StallNext = true;

            Created.Add(transport);

            Interlocked
                .Exchange(ref _next, new TaskCompletionSource<InMemoryManagedRawConnection>(
                    TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult(transport);

            return transport;
        }
    }

    private static (AdsRawChannel Channel, TransportSource Source, FakeTimeProvider Clock) Create(
        int retryCount = 1, int timeoutMs = 5000)
    {
        var source = new TransportSource();
        var clock = new FakeTimeProvider();
        var channel = new AdsRawChannel(
            "1.2.3.4.5.6", 0xFFFF, source.Create,
            new AdsRawChannelOptions { RetryCount = retryCount, TimeoutMs = timeoutMs },
            NullLogger.Instance, clock);
        return (channel, source, clock);
    }

    [Fact]
    public async Task Timeout_RetriesOnAFreshTransport()
    {
        var (channel, source, clock) = Create(retryCount: 1);
        source.StallFirst = 1;        // first transport stalls; the retry gets a fresh one
        source.Seed(0x11, 1, [7]);    // which answers, because the device still has the data

        var pending = channel.ReadAsync(0x11, 1, new byte[1], CancellationToken.None);
        await WaitForParkedAsync(source.Created[0], pending);
        clock.Advance(TimeSpan.FromMilliseconds(5001));

        var read = await pending;

        Assert.Equal(2, source.Created.Count);
        Assert.True(source.Created[0].Disposed);      // stalled transport was dropped
        Assert.False(source.Created[1].Disposed);
        Assert.Equal(1, read);
    }

    [Fact]
    public async Task ExhaustedRetries_ThrowTimeoutException()
    {
        var (channel, source, clock) = Create(retryCount: 1);
        source.StallFirst = 2;        // both attempts stall

        var pending = channel.ReadAsync(0x11, 1, new byte[1], CancellationToken.None);
        var retryTransport = source.NextTransport;   // captured before the retry creates it

        await WaitForParkedAsync(source.Created[0], pending);
        clock.Advance(TimeSpan.FromMilliseconds(5001));

        await AdvancePastParkedAttemptAsync(clock, retryTransport, pending);

        await Assert.ThrowsAsync<TimeoutException>(() => pending);
        Assert.Equal(2, source.Created.Count);
    }

    /// <summary>
    /// Advances past one attempt's bound, but only once that attempt has actually
    /// parked: the bound is scheduled when the attempt STARTS, so moving the clock
    /// first would schedule it beyond a clock that never moves again.
    /// </summary>
    /// <remarks>
    /// Each wait races <paramref name="pending"/> so that a channel which answers
    /// instead of retrying — the regression this test exists to catch — makes the
    /// assertion fail with what actually happened rather than hanging here.
    /// </remarks>
    private static async Task AdvancePastParkedAttemptAsync(
        FakeTimeProvider clock,
        Task<InMemoryManagedRawConnection> attemptTransport,
        Task pending)
    {
        if (await Task.WhenAny(attemptTransport, pending).ConfigureAwait(false) != attemptTransport)
            return;

        var transport = await attemptTransport.ConfigureAwait(false);
        if (await Task.WhenAny(transport.Stalled, pending).ConfigureAwait(false) != transport.Stalled)
            return;

        clock.Advance(TimeSpan.FromMilliseconds(5001));
    }

    [Fact]
    public async Task RetryCountZero_MakesExactlyOneAttempt()
    {
        var (channel, source, clock) = Create(retryCount: 0);
        source.StallFirst = 1;

        var pending = channel.ReadAsync(0x11, 1, new byte[1], CancellationToken.None);
        await WaitForParkedAsync(source.Created[0], pending);
        clock.Advance(TimeSpan.FromMilliseconds(5001));

        await Assert.ThrowsAsync<TimeoutException>(() => pending);
        Assert.Single(source.Created);
    }

    [Fact]
    public async Task AdsErrorAnswer_IsNotRetried()
    {
        var (channel, source, _) = Create(retryCount: 3);

        await Assert.ThrowsAsync<AdsErrorException>(
            () => channel.ReadAsync(0x11, 1, new byte[1], CancellationToken.None));

        // One transport, one attempt: an answer is not a failure.
        Assert.Single(source.Created);
        Assert.False(source.Created[0].Disposed);
    }

    [Fact]
    public async Task ExplicitTimeout_OverridesTheConfiguredBound()
    {
        // The explicit bound is the LONGER of the two on purpose. The call then has
        // to SURVIVE the configured bound and die on the explicit one, so the test
        // can tell which bound ended it — and every regression ends the call at one
        // bound or the other, so none of them can hang this test.
        var (channel, source, clock) = Create(retryCount: 0, timeoutMs: 200);
        source.StallFirst = 1;

        var pending = channel.ReadAsync(
            0x11, 1, new byte[1], TimeSpan.FromMilliseconds(60_000), CancellationToken.None);
        await WaitForParkedAsync(source.Created[0], pending);

        clock.Advance(TimeSpan.FromMilliseconds(201));     // past the CONFIGURED bound
        Assert.False(pending.IsCompleted);                 // …which does not apply here

        clock.Advance(TimeSpan.FromMilliseconds(60_000));  // past the EXPLICIT bound

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => pending);
        Assert.Contains("60000 ms", ex.Message);
    }

    /// <summary>
    /// Waits for <paramref name="transport"/> to park in its stall, but gives up
    /// the moment <paramref name="pending"/> completes — a channel that answers
    /// instead of parking then fails the assertion that follows rather than
    /// hanging here.
    /// </summary>
    private static Task WaitForParkedAsync(InMemoryManagedRawConnection transport, Task pending) =>
        Task.WhenAny(transport.Stalled, pending);
}
