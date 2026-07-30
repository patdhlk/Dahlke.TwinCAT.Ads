using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TwinCAT.Ads;
using Dahlke.TwinCAT.Ads.Tests.Fakes;

namespace Dahlke.TwinCAT.Ads.Tests.Contract;

/// <summary>
/// ONE shared behavioural contract for the raw channel data plane, run against
/// every implementation a consumer can end up holding.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="AdsConnectionContractTests"/> and exists for the same
/// reason: the simulated store and the in-memory double implement the documented
/// semantics as SEPARATE code, so a change to one that the other does not match
/// fails a shared [Fact] rather than waiting for review to catch it. If a fact
/// here cannot be satisfied by an implementation, that is a behavioural FINDING
/// to surface — not a per-class carve-out.
/// </para>
/// <para>
/// <b>Known, deliberate non-divergence — ReadWrite.</b> A prior review flagged
/// that <see cref="InMemoryManagedRawConnection.ReadWriteAsync"/> echoes back
/// exactly what was written, so it cannot model a ReadWrite whose result differs
/// from its input. <see cref="SimulatedRawConnection.ReadWriteAsync"/> does the
/// SAME thing — write then read the same slot — per its own documented remarks
/// ("a convention, not protocol emulation"). Neither implementation can express a
/// differing result, so <see cref="ReadWrite_WritesSourceThenReturnsTheSlot"/>
/// below asserts only the echo both implementations actually share; no fact here
/// depends on a diverging ReadWrite result.
/// </para>
/// </remarks>
public abstract class AdsRawChannelContractTests
{
    /// <summary>Supplies the channel under test plus a way to seed its store.</summary>
    protected sealed record RawContractHarness(
        IAdsRawChannel Channel, Action<uint, uint, byte[]> Seed);

    protected abstract RawContractHarness CreateHarness();

    [Fact]
    public async Task UnseededRead_ThrowsDeviceInvalidOffset()
    {
        var harness = CreateHarness();

        var ex = await Assert.ThrowsAsync<AdsErrorException>(
            () => harness.Channel.ReadAsync(0x11, 4242, new byte[4], CancellationToken.None));

        Assert.Equal(AdsErrorCode.DeviceInvalidOffset, ex.ErrorCode);
    }

    [Fact]
    public async Task SeededRead_ReturnsTheSeededBytes()
    {
        var harness = CreateHarness();
        harness.Seed(0x11, 1001, [1, 2, 3, 4]);

        var buffer = new byte[4];
        var read = await harness.Channel.ReadAsync(0x11, 1001, buffer, CancellationToken.None);

        Assert.Equal(4, read);
        Assert.Equal([1, 2, 3, 4], buffer);
    }

    [Fact]
    public async Task SeededShorterThanDestination_ReturnsTheSeededLength()
    {
        var harness = CreateHarness();
        harness.Seed(0x11, 1001, [9, 9]);

        var read = await harness.Channel.ReadAsync(0x11, 1001, new byte[8], CancellationToken.None);

        Assert.Equal(2, read);
    }

    [Fact]
    public async Task SeededLongerThanDestination_ReturnsTheDestinationLength()
    {
        var harness = CreateHarness();
        harness.Seed(0x11, 1001, [1, 2, 3, 4, 5, 6]);

        var read = await harness.Channel.ReadAsync(0x11, 1001, new byte[2], CancellationToken.None);

        Assert.Equal(2, read);
    }

    [Fact]
    public async Task Write_CreatesTheSlot_AndRoundTrips()
    {
        var harness = CreateHarness();

        await harness.Channel.WriteAsync(0x4020, 7, new byte[] { 5, 6 }, CancellationToken.None);

        var buffer = new byte[2];
        var read = await harness.Channel.ReadAsync(0x4020, 7, buffer, CancellationToken.None);
        Assert.Equal(2, read);
        Assert.Equal([5, 6], buffer);
    }

    [Fact]
    public async Task ReadWrite_WritesSourceThenReturnsTheSlot()
    {
        var harness = CreateHarness();

        var buffer = new byte[3];
        var read = await harness.Channel.ReadWriteAsync(
            0x7000, 0, buffer, new byte[] { 7, 8, 9 }, CancellationToken.None);

        Assert.Equal(3, read);
        Assert.Equal([7, 8, 9], buffer);
    }

    [Fact]
    public async Task ReadState_ReportsRun()
    {
        var harness = CreateHarness();

        var state = await harness.Channel.ReadStateAsync(CancellationToken.None);

        Assert.Equal(AdsState.Run, state.AdsState);
    }

    [Fact]
    public async Task Subscription_FiresOnWriteToTheWatchedSlot()
    {
        var harness = CreateHarness();

        var received = new List<byte[]>();
        await harness.Channel.SubscribeAsync(
            0x11, 1001, 1, 100, data => received.Add(data.ToArray()), CancellationToken.None);

        await harness.Channel.WriteAsync(0x11, 1001, new byte[] { 42 }, CancellationToken.None);

        Assert.Single(received);
        Assert.Equal([42], received[0]);
    }

    /// <remarks>
    /// Pins ON-CHANGE semantics for raw notifications: the real transport
    /// registers <c>AdsTransMode.OnChange</c>, so a device only notifies when the
    /// slot's CONTENT changes — rewriting the same bytes (even from a fresh
    /// buffer) is silent, and a subsequent different write fires again. Both
    /// simulated data planes must agree with the hardware they stand in for.
    /// </remarks>
    [Fact]
    public async Task Subscription_DoesNotFireWhenTheWrittenBytesAreUnchanged()
    {
        var harness = CreateHarness();

        var received = new List<byte[]>();
        await harness.Channel.SubscribeAsync(
            0x11, 1001, 1, 100, data => received.Add(data.ToArray()), CancellationToken.None);

        await harness.Channel.WriteAsync(0x11, 1001, new byte[] { 42 }, CancellationToken.None);
        await harness.Channel.WriteAsync(0x11, 1001, new byte[] { 42 }, CancellationToken.None); // same bytes, fresh buffer
        await harness.Channel.WriteAsync(0x11, 1001, new byte[] { 43 }, CancellationToken.None);

        Assert.Equal(2, received.Count);
        Assert.Equal([42], received[0]);
        Assert.Equal([43], received[1]);
    }

    /// <remarks>
    /// <b>Pins <see cref="AdsRawChannel"/>'s own registry gate, not either
    /// transport's notification removal.</b> <c>AdsRawChannel.Unsubscribe</c>
    /// removes the channel's registry entry SYNCHRONOUSLY, before the
    /// transport-level removal (a fire-and-forget round trip) even starts, so
    /// <c>AdsRawChannel.Deliver</c>'s registry check always wins the race
    /// regardless of whether the underlying transport's own removal succeeds,
    /// fails, or is a no-op. This fact therefore CANNOT, by construction, catch a
    /// transport-level regression in either <see cref="SimulatedRawConnection"/>
    /// or <see cref="InMemoryManagedRawConnection"/> in isolation — passing on
    /// both derived classes here proves the shared channel logic, not agreement
    /// between the two data planes. Verified empirically: mutating either
    /// transport's <c>RemoveNotificationAsync</c> to a no-op leaves this fact
    /// green on both harnesses; only breaking <c>AdsRawChannel</c>'s own gate
    /// turns it red (see task-8-report.md, "Mutation testing"). See also
    /// <c>AdsRawChannelSubscriptionTests.DisposedSubscription_StopsDelivering_EvenWhenTheTransportRemovalFails</c>,
    /// which demonstrates the same gate directly against the removal-failure case.
    /// </remarks>
    [Fact]
    public async Task DisposedSubscription_StopsFiring()
    {
        var harness = CreateHarness();

        var count = 0;
        var handle = await harness.Channel.SubscribeAsync(
            0x11, 1001, 1, 100, _ => count++, CancellationToken.None);

        await harness.Channel.WriteAsync(0x11, 1001, new byte[] { 1 }, CancellationToken.None);
        handle.Dispose();
        await harness.Channel.WriteAsync(0x11, 1001, new byte[] { 2 }, CancellationToken.None);

        Assert.Equal(1, count);
    }

    /// <remarks>
    /// Pins that a throwing subscriber cannot stop later notifications. Two nets
    /// now hold this: <see cref="AdsRawChannel"/>'s own handler try/catch in
    /// <c>Deliver</c> (the logged one — the callback each transport registers is
    /// always <c>data =&gt; Deliver(record, data)</c>), and beneath it the shared
    /// <see cref="SubscriberRegistry{TKey, TValue}"/>'s per-callback isolation,
    /// which both raw transports compose. The registry's isolation is pinned
    /// directly by its own unit tests; this fact pins the channel-level outcome
    /// on both harnesses.
    /// </remarks>
    [Fact]
    public async Task ThrowingHandler_DoesNotStopLaterNotifications()
    {
        var harness = CreateHarness();

        var calls = 0;
        await harness.Channel.SubscribeAsync(0x11, 1001, 1, 100, _ =>
        {
            calls++;
            throw new InvalidOperationException("subscriber bug");
        }, CancellationToken.None);

        await harness.Channel.WriteAsync(0x11, 1001, new byte[] { 1 }, CancellationToken.None);
        await harness.Channel.WriteAsync(0x11, 1001, new byte[] { 2 }, CancellationToken.None);

        Assert.Equal(2, calls);
    }
}

/// <summary>The channel over the simulated byte store — the production sim path.</summary>
/// <remarks>
/// The store is created directly (not via
/// <see cref="IAdsRawChannelFactory.TryGetSimulated"/>) so the harness can seed it
/// through the same <see cref="SimulatedRawStore.Seed"/> entry point a real
/// consumer reaches, while <see cref="AdsRawChannel"/>'s connection factory hands
/// out a fresh <see cref="SimulatedRawConnection"/> wrapping that store — mirroring
/// how the production factory keeps one store per target behind possibly many
/// transport instances.
/// </remarks>
public sealed class SimulatedRawChannelContractTests : AdsRawChannelContractTests
{
    protected override RawContractHarness CreateHarness()
    {
        var store = new SimulatedRawStore();
        var channel = new AdsRawChannel(
            "1.2.3.4.5.6", 0xFFFF, (netId, port) => new SimulatedRawConnection(netId, port, store),
            new AdsRawChannelOptions { Mode = ConnectionMode.Simulated },
            NullLogger.Instance, new FakeTimeProvider());

        return new RawContractHarness(channel, (ig, io, data) => store.Seed(ig, io, data));
    }
}

/// <summary>
/// The channel over an INDEPENDENT in-memory double, so the suite pins both the
/// facade plumbing and the documented data-plane spec without the two sharing code.
/// </summary>
public sealed class InMemoryRawChannelContractTests : AdsRawChannelContractTests
{
    protected override RawContractHarness CreateHarness()
    {
        var transport = new InMemoryManagedRawConnection();
        var channel = new AdsRawChannel(
            "1.2.3.4.5.6", 0xFFFF, (_, _) => transport,
            new AdsRawChannelOptions(),
            NullLogger.Instance, new FakeTimeProvider());

        return new RawContractHarness(channel, (ig, io, data) => transport.Seed(ig, io, data));
    }
}
