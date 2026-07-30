namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Unit tests for <see cref="OwnedLoopCancellation"/> — the one owner of the
/// loop-teardown discipline: teardown paths request stop (cancel-only, any
/// number of times, from any thread, never throwing); the owning loop alone
/// retires the signal, in its own <c>finally</c>, after it has exited; and a
/// signal nobody retires (a pure shutdown flag) is a supported shape. Previously
/// this discipline was implemented three times — the pool's reconnect loops, the
/// raw factory's sweeper, the raw channel's shutdown signal — each defending
/// itself with a long comment citing the same root-cause hang.
/// </summary>
public class OwnedLoopCancellationTests
{
    [Fact]
    public void RequestStop_CancelsTheToken_AndIsIdempotent()
    {
        var signal = new OwnedLoopCancellation();
        Assert.False(signal.IsStopRequested);

        signal.RequestStop();
        signal.RequestStop(); // second and later requests are no-ops, never throws

        Assert.True(signal.IsStopRequested);
        Assert.True(signal.Token.IsCancellationRequested);
    }

    [Fact]
    public void RequestStop_AfterOwnerRetired_IsASafeNoOp()
    {
        // The abnormal-exit case: the owning loop died (throwing logger, bug),
        // retired its signal in its finally, and only LATER does a teardown path
        // request stop. Cancelling a disposed source throws — the primitive must
        // absorb the situation: a retired signal means the loop already exited,
        // so there is nothing left to cancel.
        var signal = new OwnedLoopCancellation();
        signal.OwnerRetire();

        signal.RequestStop(); // must not throw

        // The loop is gone either way; the stop request is still recorded.
        Assert.True(signal.IsStopRequested);
    }

    [Fact]
    public void NormalShutdownSequence_StopThenRetire_ThenLateStop()
    {
        // The normal host sequence: StopAsync requests stop, the loop wakes and
        // retires in its finally, then Dispose (the second teardown path)
        // requests stop again. No step may throw.
        var signal = new OwnedLoopCancellation();

        signal.RequestStop();     // StopAsync
        signal.OwnerRetire();     // loop's finally
        signal.OwnerRetire();     // idempotent
        signal.RequestStop();     // Dispose

        Assert.True(signal.IsStopRequested);
    }

    [Fact]
    public void Token_RemainsReadableAfterRetire()
    {
        var signal = new OwnedLoopCancellation();
        var token = signal.Token;

        signal.RequestStop();
        signal.OwnerRetire();

        // Observing the token after the source is retired must stay safe — the
        // raw channel's restore path reads its shutdown token per record.
        Assert.True(token.IsCancellationRequested);
        Assert.True(signal.IsStopRequested);
    }

    [Fact]
    public void NeverRetiredSignal_IsASupportedShape()
    {
        // The raw channel's shutdown signal has no owning loop: it is raised at
        // most once and deliberately never retired (a source with no timer and
        // no lingering registrations may live undisposed; cancel-after-dispose
        // is the hazard, and nobody can dispose it).
        var signal = new OwnedLoopCancellation();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(signal.Token);
        signal.RequestStop();

        Assert.True(linked.IsCancellationRequested);
    }

    [Fact]
    public async Task ConcurrentRequestsAndRetire_NeverThrow()
    {
        // The race the three hand-rolled copies each argued about in prose:
        // multiple teardown paths requesting stop while the owner retires the
        // moment it observes cancellation.
        for (var i = 0; i < 500; i++)
        {
            var signal = new OwnedLoopCancellation();
            var owner = Task.Run(() =>
            {
                signal.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                signal.OwnerRetire();
            });
            var stoppers = Enumerable.Range(0, 4)
                .Select(_ => Task.Run(signal.RequestStop))
                .ToArray();

            await Task.WhenAll(stoppers.Append(owner));
            Assert.True(signal.IsStopRequested);
        }
    }
}
