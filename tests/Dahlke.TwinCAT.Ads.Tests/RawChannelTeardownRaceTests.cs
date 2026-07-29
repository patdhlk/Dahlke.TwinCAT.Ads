using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Reproduction harness for the ownership-ambiguity failure class behind
/// #9/#13/#15, aimed at the raw channel path.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure class.</b> <see cref="CancellationTokenSource.Dispose()"/> is not
/// safe to call while another thread is inside <see cref="CancellationTokenSource.Cancel()"/>
/// on the same source. Exactly one caller runs the registered callbacks; a second
/// <c>Cancel()</c> sees the source already cancelling and returns IMMEDIATELY
/// without waiting for them. If that second caller then disposes, it frees the
/// registration list while the winner is still walking it, and a pending
/// registration is dropped and never invoked. A loop parked on
/// <c>Task.Delay(..., cts.Token)</c> then never wakes, its task never finishes, and
/// <c>StopAsync</c> waits on it forever. Refcount-free ownership plus durable
/// re-registration plus idle eviction is the same shape, which is why the raw path
/// needs its own harness rather than inheriting the pool's.
/// </para>
/// <para>
/// <b>Reproduction recipe.</b> The original hang needed Linux, <c>--cpus=2</c>,
/// and the WHOLE suite in one process before it would surface — it hung ~15-20%
/// of runs before the fix and 0 of 42 after. Isolating any one of those
/// conditions gives a false all-clear, so a green run of this file alone on a
/// developer machine proves very little. Use the recipe recorded on
/// <c>AdsConnectionPoolTests.ConcurrentStopAndDispose_DoNotThrow</c>:
/// </para>
/// <code>
///   docker run --rm --cpus=2 -v "$PWD":/src -w /src \
///     mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
///       dotnet build tests/Dahlke.TwinCAT.Ads.Tests/Dahlke.TwinCAT.Ads.Tests.csproj -c Release -f net10.0
///       for i in $(seq 1 30); do
///         dotnet vstest tests/Dahlke.TwinCAT.Ads.Tests/bin/Release/net10.0/Dahlke.TwinCAT.Ads.Tests.dll
///       done'
/// </code>
/// <para>
/// Note <c>dotnet test</c> silently no-ops in the <c>sdk:10.0</c> container —
/// it exits 0 with no output. Use <c>dotnet vstest</c> against the built test
/// assembly instead, or a green run means nothing at all.
/// </para>
/// <para>
/// <b>Each test here is verified capable of failing</b>, by reintroducing the
/// defect it guards and watching it go red under that recipe. The mutation for
/// each is named in its own remarks. A stress test that cannot fail reads exactly
/// like a fix; if one of these stops being red under its stated mutation it has
/// stopped being evidence of anything and must be repaired, not trusted.
/// </para>
/// </remarks>
public class RawChannelTeardownRaceTests
{
    private const string NetId = "1.2.3.4.5.6";
    private const int Port = 0xFFFF;
    private const uint Ig = 0x11;
    private const uint Io = 1;

    /// <summary>
    /// Generous enough that thread-pool starvation on a two-core box cannot trip
    /// it, short enough that a lost cancellation registration — which parks a
    /// teardown forever, not merely for a while — always does. Matches the bound
    /// the pool's own race test uses.
    /// </summary>
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(15);

    private static AdsRawChannelFactory CreateFactory(TimeProvider clock, int idleEvictionMs = 60_000)
    {
        var options = new TwinCatAdsOptions();
        options.RawChannels.Mode = ConnectionMode.Simulated;
        options.RawChannels.IdleEvictionMs = idleEvictionMs;
        return new AdsRawChannelFactory(Options.Create(options), NullLoggerFactory.Instance, clock);
    }

    /// <summary>
    /// <c>StopAsync</c> and <c>Dispose</c> running concurrently must neither throw
    /// nor hang — the exact collision the host's shutdown path and the container's
    /// disposal path make on every application stop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the clock is fake.</b> The sweeper parks on
    /// <c>Task.Delay(SweepInterval, clock, cts.Token)</c>. Against the system clock
    /// a dropped cancellation registration costs the sweep interval and then
    /// resolves itself, so the symptom is a bounded stall that a wall-clock
    /// assertion can only catch by guessing a threshold between "slow box" and
    /// "broken". A <see cref="FakeTimeProvider"/> that is never advanced makes the
    /// delay completable ONLY by cancellation, so a dropped registration is a
    /// permanent hang and the assertion below is exact. The race being reproduced
    /// is between <c>Cancel</c> and <c>Dispose</c> on the token source and is
    /// entirely clock-independent; the fake clock changes the consequence's
    /// duration, never the window.
    /// </para>
    /// <para>
    /// <b>Verified capable of failing</b> by reintroducing the #15 ownership split:
    /// drop the <see cref="Interlocked.Exchange{T}(ref T, T)"/> from
    /// <c>AdsRawChannelFactory.RequestSweeperStop</c> so both teardown paths see the
    /// same source, and add <c>_sweeperCts?.Dispose()</c> to <c>Dispose</c>. That is
    /// the pre-#15 shape — cancel-only from one path, cancel-and-dispose from the
    /// other, one shared field. It fails in milliseconds, on a dev box, with
    /// <see cref="ObjectDisposedException"/> out of
    /// <see cref="CancellationTokenSource.Cancel()"/>: the LOUD symptom.
    /// </para>
    /// <para>
    /// <b>The quiet symptom needs the container.</b> Wrapping that same
    /// <c>Cancel()</c> in <c>try/catch (ObjectDisposedException)</c> reproduces what
    /// 0.5.1 and 0.5.2 actually shipped — the exception silenced, the dropped
    /// registration still there. Under that mutation this test stays GREEN on macOS
    /// at any core count and goes red in 3 of 12 container runs with
    /// <see cref="TimeoutException"/>, matching the ~15-20% the original hang
    /// managed. That gap between the two mutations is the whole reason the recipe
    /// above is written down: the symptom that survived two fixes is the one a dev
    /// box cannot see.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ConcurrentStopAndDispose_DoNotThrowOrHang()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var factory = CreateFactory(new FakeTimeProvider());
            await factory.StartAsync(CancellationToken.None);

            // A live transport, so both teardown paths have real work to contend
            // over on the channel's gate rather than racing on the sweeper alone.
            var channel = factory.Get(NetId, Port);
            Assert.True(factory.TryGetSimulated(NetId, Port, out var sim));
            sim.Seed(Ig, Io, [1]);
            await channel.ReadAsync(Ig, Io, new byte[1], CancellationToken.None);

            // The barrier is what makes the window reachable at all: without it the
            // two Task.Run bodies are scheduled far enough apart that one teardown
            // has finished before the other starts, and the test proves nothing.
            using var barrier = new Barrier(2);

            var stop = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await factory.StopAsync(CancellationToken.None);
            });

            var dispose = Task.Run(() =>
            {
                barrier.SignalAndWait();
                factory.Dispose();
            });

            // Two assertions in one await: neither path may fault (WhenAll rethrows),
            // and neither may hang (WaitAsync throws TimeoutException). Both symptoms
            // of the ownership split have been seen in this codebase.
            await Task.WhenAll(stop, dispose).WaitAsync(RealTimeout);
        }
    }

    /// <summary>
    /// Idle eviction must never pull a transport out from under a call that is
    /// already using it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IdleEvictionMs</c> is zero so every sweep ATTEMPTS an eviction rather than
    /// mostly bailing on the idle check. That turns a rare coincidence into the
    /// common case: the reader rebuilds its transport constantly and the sweeper
    /// tries to claim it constantly, which is the only way the sub-microsecond gap
    /// between <c>AdsRawChannel</c>'s in-flight registration and its transport read
    /// gets hit inside a bounded run.
    /// </para>
    /// <para>
    /// <b>Why this test can fail at all.</b> It relies on
    /// <c>SimulatedRawConnection</c> throwing <see cref="ObjectDisposedException"/>
    /// once its transport has been disposed. Before that was added, a disposed
    /// simulated connection kept serving reads straight out of the shared store, so
    /// this test passed whether or not eviction raced an in-flight call — it would
    /// have gone green over exactly the bug it exists to catch. If a future change
    /// makes disposal silent again, this test stops being evidence of anything.
    /// </para>
    /// <para>
    /// <b>Verified capable of failing</b> by deleting the post-claim re-check in
    /// <c>AdsRawChannel.TryEvictIfIdle</c> — the
    /// <c>if (Volatile.Read(ref _inFlight) > 0 || LiveSubscriptionCount > 0)</c>
    /// block that hands the transport back — which is the second half of the Dekker
    /// pair. The reader then reads through a disposed transport.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EvictionRacingAnInFlightOperation_DoesNotThrow()
    {
        using var factory = CreateFactory(TimeProvider.System, idleEvictionMs: 0);
        var channel = factory.Get(NetId, Port);
        Assert.True(factory.TryGetSimulated(NetId, Port, out var sim));
        sim.Seed(Ig, Io, [1]);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // Neither loop guards anything: an escaping exception IS the failure. Note
        // SweepOnce logs and swallows per-channel faults, so the sweeper side can
        // never report — the reader is the only witness, which is why it must not
        // catch either.
        var reader = Task.Run(async () =>
        {
            while (!deadline.IsCancellationRequested)
                await channel.ReadAsync(Ig, Io, new byte[1], CancellationToken.None);
        });

        var sweeper = Task.Run(() =>
        {
            while (!deadline.IsCancellationRequested)
                factory.SweepOnce();
        });

        await Task.WhenAll(reader, sweeper).WaitAsync(RealTimeout);
    }

    /// <summary>
    /// Subscribing and unsubscribing must both survive a sweep landing on the same
    /// channel at the same instant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The read at the top of each cycle is load-bearing. It leaves a live
    /// transport, so the subscribe that follows takes
    /// <c>GetOrCreateTransportAsync</c>'s fast path and registers WITHOUT holding
    /// the transport gate. The gate therefore protects nothing here, and the
    /// subscription half of the eviction handshake — publish to the registry with a
    /// full fence, then re-check that registry after claiming the transport — is the
    /// only thing standing between the subscribe and a transport disposed underneath
    /// it.
    /// </para>
    /// <para>
    /// <b>Free-running loops, not a choreographed pair.</b> An earlier version of
    /// this test ran 100 barrier-synchronised subscribe/sweep pairs and stayed GREEN
    /// under the mutation below — the subscribe reliably won the barrier and
    /// published itself before the sweep read the registry, so the window was never
    /// entered and the test was decoration. The window that matters is the few
    /// instructions between the sweeper's registry check and its transport claim;
    /// reaching it needs millions of attempts, not a hundred well-aligned ones.
    /// </para>
    /// <para>
    /// <b>Verified capable of failing</b> by deleting the post-claim re-check in
    /// <c>AdsRawChannel.TryEvictIfIdle</c> — the
    /// <c>if (Volatile.Read(ref _inFlight) > 0 || LiveSubscriptionCount > 0)</c>
    /// block that hands the transport back. <c>AddNotificationAsync</c> then lands
    /// on a disposed transport and <c>SubscribeAsync</c> throws
    /// <see cref="ObjectDisposedException"/> out of its rollback path.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SubscriptionRacingIdleEviction_DoesNotThrowOrLeak()
    {
        using var factory = CreateFactory(TimeProvider.System, idleEvictionMs: 0);
        var channel = (AdsRawChannel)factory.Get(NetId, Port);
        Assert.True(factory.TryGetSimulated(NetId, Port, out var sim));
        sim.Seed(Ig, Io, [1]);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var subscriber = Task.Run(async () =>
        {
            while (!deadline.IsCancellationRequested)
            {
                await channel.ReadAsync(Ig, Io, new byte[1], CancellationToken.None);

                var handle = await channel.SubscribeAsync(
                    Ig, Io, 1, 10, _ => { }, CancellationToken.None);

                // Only ever one subscriber, so these counts are exact rather than
                // racy: a sweep that raced either side must leave neither a lost
                // registration nor a half-removed one behind.
                Assert.Equal(1, channel.LiveSubscriptionCount);
                handle.Dispose();
                Assert.Equal(0, channel.LiveSubscriptionCount);
            }
        });

        var sweeper = Task.Run(() =>
        {
            while (!deadline.IsCancellationRequested)
                factory.SweepOnce();
        });

        await Task.WhenAll(subscriber, sweeper).WaitAsync(RealTimeout);
    }
}
