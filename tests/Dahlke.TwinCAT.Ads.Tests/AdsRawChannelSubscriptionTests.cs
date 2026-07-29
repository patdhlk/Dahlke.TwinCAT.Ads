using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Dahlke.TwinCAT.Ads.Tests.Fakes;

namespace Dahlke.TwinCAT.Ads.Tests;

public class AdsRawChannelSubscriptionTests
{
    /// <summary>The default <see cref="AdsRawChannelOptions.TimeoutMs"/>, which these tests do not override.</summary>
    private const int AttemptTimeoutMs = 5000;

    /// <summary>
    /// Hands out a NEW transport each time, recording them, so a test can prove a
    /// subscription was re-registered against the replacement.
    /// </summary>
    /// <remarks>
    /// Seeds are held HERE rather than applied to an individual transport: a device
    /// keeps its data across a reconnect, and the transport that has to answer after
    /// a drop does not exist yet at the point a test wants to arrange for it.
    /// </remarks>
    private sealed class TransportSource
    {
        private readonly List<(uint Ig, uint Io, byte[] Data)> _seeds = [];

        public List<InMemoryManagedRawConnection> Created { get; } = [];

        /// <summary>When true, every transport handed out stalls every operation.</summary>
        public bool StallEveryOperation { get; set; }

        /// <summary>
        /// Runs as each transport is handed out, INSIDE the transport gate and
        /// before any subscription is restored onto it.
        /// </summary>
        /// <remarks>
        /// The only deterministic way to act at that instant: a rebuild happens
        /// inside <c>GetOrCreateTransportAsync</c>, where a test has no other seam.
        /// </remarks>
        public Action<InMemoryManagedRawConnection>? OnCreate { get; set; }

        /// <summary>Data every transport is born with.</summary>
        public void Seed(uint ig, uint io, byte[] data) => _seeds.Add((ig, io, data));

        public IManagedRawConnection Create(string netId, int port)
        {
            var transport = new InMemoryManagedRawConnection { StallAlways = StallEveryOperation };

            foreach (var (ig, io, data) in _seeds)
                transport.Seed(ig, io, data);

            Created.Add(transport);
            OnCreate?.Invoke(transport);
            return transport;
        }
    }

    private static (AdsRawChannel Channel, TransportSource Source, FakeTimeProvider Clock) Create(
        int idleEvictionMs = 60_000)
    {
        var source = new TransportSource();
        var clock = new FakeTimeProvider();
        var channel = new AdsRawChannel(
            "1.2.3.4.5.6", 0xFFFF, source.Create,
            new AdsRawChannelOptions { IdleEvictionMs = idleEvictionMs, RetryCount = 0 },
            NullLogger.Instance, clock);
        return (channel, source, clock);
    }

    /// <summary>
    /// Forces an INVOLUNTARY transport drop: the current transport stalls one
    /// operation, that operation burns its per-attempt bound, and the channel
    /// discards the transport.
    /// </summary>
    /// <remarks>
    /// The clock is advanced only once the attempt has genuinely parked — the bound
    /// is scheduled when the attempt STARTS, so moving a fake clock first would
    /// schedule it beyond a clock that never moves again. The wait races
    /// <c>pending</c> so a channel that answers instead of stalling fails the
    /// assertion below rather than hanging here.
    /// </remarks>
    private static async Task ForceDropAsync(
        AdsRawChannel channel, InMemoryManagedRawConnection transport, FakeTimeProvider clock)
    {
        transport.StallNext = true;

        var pending = channel.ReadAsync(0x11, 1001, new byte[1], CancellationToken.None);
        await Task.WhenAny(transport.Stalled, pending);
        clock.Advance(TimeSpan.FromMilliseconds(AttemptTimeoutMs + 1));

        await Assert.ThrowsAsync<TimeoutException>(() => pending);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task Subscribe_DeliversOnChange()
    {
        var (channel, source, _) = Create();

        var received = new List<byte[]>();
        await channel.SubscribeAsync(
            0x11, 1001, length: 1, cycleTimeMs: 100,
            data => received.Add(data.ToArray()), CancellationToken.None);

        await channel.WriteAsync(0x11, 1001, new byte[] { 42 }, CancellationToken.None);

        // Single, not just non-empty: a subscribe that registers itself AND is
        // restored onto the transport it just caused to be built would deliver
        // twice, and only the count says so.
        Assert.Single(received);
        Assert.Equal([42], received[0]);
        Assert.Single(source.Created[0].LiveHandles);
    }

    [Fact]
    public async Task Subscribe_PinsTheChannelAgainstIdleEviction()
    {
        var (channel, source, clock) = Create(idleEvictionMs: 1000);

        await channel.SubscribeAsync(
            0x11, 1001, 1, 100, _ => { }, CancellationToken.None);

        clock.Advance(TimeSpan.FromMilliseconds(5000));

        Assert.False(channel.TryEvictIfIdle(TimeSpan.FromMilliseconds(1000)));
        Assert.Equal(ConnectionState.Connected, channel.State);
        Assert.False(source.Created[0].Disposed);
    }

    [Fact]
    public async Task DisposedSubscription_UnpinsTheChannel()
    {
        var (channel, _, clock) = Create(idleEvictionMs: 1000);

        var handle = await channel.SubscribeAsync(
            0x11, 1001, 1, 100, _ => { }, CancellationToken.None);
        handle.Dispose();

        clock.Advance(TimeSpan.FromMilliseconds(5000));

        // The mirror of the test above: the same sweep that was refused while the
        // subscription was live now succeeds, so the pin is about the subscription
        // and not a channel that can never be evicted.
        Assert.True(channel.TryEvictIfIdle(TimeSpan.FromMilliseconds(1000)));
    }

    [Fact]
    public async Task Subscription_SurvivesAnInvoluntaryDrop_AndReregistersExactlyOnce()
    {
        var (channel, source, clock) = Create();
        source.Seed(0x11, 1001, [1]);   // the device keeps its data across the reconnect

        var received = new List<byte[]>();
        await channel.SubscribeAsync(
            0x11, 1001, 1, 100, data => received.Add(data.ToArray()), CancellationToken.None);

        Assert.Single(source.Created[0].LiveHandles);

        await ForceDropAsync(channel, source.Created[0], clock);

        // Next operation builds a fresh transport and restores the subscription.
        await channel.ReadAsync(0x11, 1001, new byte[1], CancellationToken.None);

        Assert.Equal(2, source.Created.Count);
        Assert.Single(source.Created[1].LiveHandles);   // exactly once, not twice

        await channel.WriteAsync(0x11, 1001, new byte[] { 99 }, CancellationToken.None);
        Assert.Contains(received, r => r.SequenceEqual(new byte[] { 99 }));

        // …and exactly once means the handler ran once for that write, too.
        Assert.Single(received);
    }

    [Fact]
    public async Task Restore_SurvivesCancellationOfTheCallThatTriggeredTheRebuild()
    {
        var (channel, source, clock) = Create();
        source.Seed(0x11, 1001, [1]);

        await channel.SubscribeAsync(0x11, 1001, 1, 100, _ => { }, CancellationToken.None);
        await ForceDropAsync(channel, source.Created[0], clock);

        // A rebuild is triggered by whichever call next touches the channel, and it
        // carries THAT caller's token — an aborted HTTP request, say. Cancel it at
        // the instant the replacement is built, which is immediately before the
        // restore runs.
        using var cts = new CancellationTokenSource();
        source.OnCreate = _ => cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => channel.ReadAsync(0x11, 1001, new byte[1], cts.Token));

        // That caller is gone; the subscription is not. Threading their token into
        // the restore would unregister every subscription on the channel — and
        // permanently, since those same live subscriptions pin the channel against
        // idle eviction, so nothing would ever rebuild the transport again.
        Assert.Equal(2, source.Created.Count);
        Assert.Single(source.Created[1].LiveHandles);
        Assert.Equal(1, channel.LiveSubscriptionCount);
    }

    [Fact]
    public async Task DisposingDuringARestore_OrphansNoNotification()
    {
        var (channel, source, clock) = Create();
        source.Seed(0x11, 1001, [1]);

        var handle = await channel.SubscribeAsync(
            0x11, 1001, 1, 100, _ => { }, CancellationToken.None);

        await ForceDropAsync(channel, source.Created[0], clock);

        // Dispose from INSIDE the replacement's registration call — the window
        // where a real ADS round trip is outstanding. The record leaves the
        // registry before the handle it is about to be given even exists.
        source.OnCreate = t => t.OnAddNotification = handle.Dispose;

        await channel.ReadAsync(0x11, 1001, new byte[1], CancellationToken.None);

        Assert.Equal(0, channel.LiveSubscriptionCount);

        // The registration the restore created belongs to nobody: no handle holds
        // it and no registry entry names it. Committing it blindly would leave the
        // device pushing a notification for the whole life of this transport, which
        // the remaining subscriptions can pin open indefinitely.
        Assert.Empty(source.Created[1].LiveHandles);
    }

    [Fact]
    public async Task Subscribe_AgainstAnUnresponsiveTarget_TimesOutOnTheConfiguredBound()
    {
        var (channel, source, clock) = Create();
        source.StallEveryOperation = true;   // the target never answers

        var pending = channel.SubscribeAsync(
            0x11, 1001, 1, 100, _ => { }, CancellationToken.None);

        // One bound covers the whole registration, so one advance ends it. Without
        // it the only bound is AdsClient's own 5 s default, which is neither this
        // value nor a TimeoutException.
        await Task.WhenAny(source.Created[0].Stalled, pending);
        clock.Advance(TimeSpan.FromMilliseconds(AttemptTimeoutMs + 1));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => pending);
        Assert.Contains($"{AttemptTimeoutMs} ms", ex.Message);

        // Rolled back: a failed subscribe must not leave a phantom entry pinning
        // the channel against eviction forever.
        Assert.Equal(0, channel.LiveSubscriptionCount);
        Assert.True(channel.TryEvictIfIdle(TimeSpan.Zero));
    }

    [Fact]
    public async Task DisposedSubscription_IsNotReregisteredAfterADrop()
    {
        var (channel, source, clock) = Create();
        source.Seed(0x11, 1001, [1]);

        var handle = await channel.SubscribeAsync(
            0x11, 1001, 1, 100, _ => { }, CancellationToken.None);
        handle.Dispose();

        await ForceDropAsync(channel, source.Created[0], clock);

        await channel.ReadAsync(0x11, 1001, new byte[1], CancellationToken.None);

        Assert.Equal(2, source.Created.Count);
        Assert.Empty(source.Created[1].LiveHandles);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var (channel, _, _) = Create();

        var handle = await channel.SubscribeAsync(
            0x11, 1001, 1, 100, _ => { }, CancellationToken.None);

        handle.Dispose();
        handle.Dispose();   // must not throw

        Assert.Equal(0, channel.LiveSubscriptionCount);
    }

    [Fact]
    public async Task DisposedSubscription_StopsDelivering_EvenWhenTheTransportRemovalFails()
    {
        var (channel, source, _) = Create();

        var received = 0;
        var handle = await channel.SubscribeAsync(
            0x11, 1001, 1, 100, _ => received++, CancellationToken.None);

        // Removal is a round trip and disposal does not wait for it. Make it FAIL
        // outright, so the transport keeps its registration and keeps delivering:
        // the documented promise — no handler fires once disposal has returned — is
        // then carried by the channel's registry alone, which is the point.
        source.Created[0].FailNextWith = new InvalidOperationException("remove refused");
        handle.Dispose();

        await channel.WriteAsync(0x11, 1001, new byte[] { 7 }, CancellationToken.None);

        Assert.Single(source.Created[0].LiveHandles);   // the transport really did keep it
        Assert.Equal(0, received);
    }

    [Fact]
    public async Task DisposingAHandle_AfterItsTransportIsGone_DoesNotThrow()
    {
        // Deliberately NOT the in-memory double: SimulatedRawConnection is the
        // production transport a simulated host actually runs, and its removal is
        // not an async method, so once it is disposed the ObjectDisposedException
        // escapes the CALL rather than landing in the returned task. Disposal is
        // documented safe and idempotent, which has to hold for both shapes.
        var store = new SimulatedRawStore();
        var channel = new AdsRawChannel(
            "1.2.3.4.5.6", 0xFFFF,
            (netId, port) => new SimulatedRawConnection(netId, port, store),
            new AdsRawChannelOptions { RetryCount = 0 },
            NullLogger.Instance, new FakeTimeProvider());

        var handle = await channel.SubscribeAsync(
            0x11, 1001, 1, 100, _ => { }, CancellationToken.None);

        channel.Shutdown();   // host shutdown disposes the transport out from under it

        handle.Dispose();     // must not throw

        Assert.Equal(0, channel.LiveSubscriptionCount);
    }

    [Fact]
    public async Task ThrowingHandler_DoesNotTearDownTheSubscription()
    {
        var (channel, _, _) = Create();

        var calls = 0;
        await channel.SubscribeAsync(0x11, 1001, 1, 100, _ =>
        {
            calls++;
            throw new InvalidOperationException("subscriber bug");
        }, CancellationToken.None);

        await channel.WriteAsync(0x11, 1001, new byte[] { 1 }, CancellationToken.None);
        await channel.WriteAsync(0x11, 1001, new byte[] { 2 }, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(1, channel.LiveSubscriptionCount);
    }

    [Fact]
    public async Task MultipleSubscriptions_AreIndependent()
    {
        var (channel, _, _) = Create();

        var a = 0;
        var b = 0;
        var handleA = await channel.SubscribeAsync(0x11, 1, 1, 100, _ => a++, CancellationToken.None);
        await channel.SubscribeAsync(0x11, 1, 1, 100, _ => b++, CancellationToken.None);

        await channel.WriteAsync(0x11, 1, new byte[] { 1 }, CancellationToken.None);
        handleA.Dispose();
        await channel.WriteAsync(0x11, 1, new byte[] { 2 }, CancellationToken.None);

        Assert.Equal(1, a);
        Assert.Equal(2, b);
    }
}
