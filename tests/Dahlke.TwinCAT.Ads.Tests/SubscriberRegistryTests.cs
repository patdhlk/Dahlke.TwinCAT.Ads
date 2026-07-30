namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Unit tests for <see cref="SubscriberRegistry{TKey, TValue}"/> — the one owner
/// of the simulated/in-memory subscriber mechanics: per-key registration,
/// snapshot-then-fire outside the lock, per-callback exception isolation, and
/// idempotent disposal. Composed by every simulated/in-memory data plane; the
/// mechanics are pinned here once.
/// </summary>
public class SubscriberRegistryTests
{
    [Fact]
    public void Fire_InvokesSubscribersOfThatKeyOnly()
    {
        var registry = new SubscriberRegistry<string, object?>();
        var received = new List<(string Key, object? Value)>();

        using var sub = registry.Subscribe("MAIN.A", (k, v) => received.Add((k, v)));
        using var other = registry.Subscribe("MAIN.B", (k, v) => received.Add(("wrong-" + k, v)));

        registry.Fire("MAIN.A", 7);

        Assert.Equal([("MAIN.A", (object?)7)], received);
    }

    [Fact]
    public void MultipleSubscribersOnOneKey_AreIndependent()
    {
        var registry = new SubscriberRegistry<string, object?>();
        int first = 0, second = 0;

        var sub1 = registry.Subscribe("MAIN.A", (_, _) => first++);
        using var sub2 = registry.Subscribe("MAIN.A", (_, _) => second++);

        registry.Fire("MAIN.A", 1);
        sub1.Dispose();
        sub1.Dispose(); // idempotent
        registry.Fire("MAIN.A", 2);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
    }

    [Fact]
    public void ThrowingCallback_IsIsolated_AndReportedThroughTheHook()
    {
        var reported = new List<(string Key, Exception Error)>();
        var registry = new SubscriberRegistry<string, object?>(
            onCallbackError: (k, ex) => reported.Add((k, ex)));
        var survivorFired = 0;

        using var bad = registry.Subscribe("MAIN.A",
            (_, _) => throw new InvalidOperationException("subscriber bug"));
        using var good = registry.Subscribe("MAIN.A", (_, _) => survivorFired++);

        registry.Fire("MAIN.A", 1);

        Assert.Equal(1, survivorFired);
        var (key, error) = Assert.Single(reported);
        Assert.Equal("MAIN.A", key);
        Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public void KeyComparer_MakesSubscriptionsCaseInsensitive()
    {
        // PLC symbol paths are documented case-insensitive; the subscriber side
        // must agree with the store side or a subscriber registered under one
        // casing silently never fires for a writer using another.
        var registry = new SubscriberRegistry<string, object?>(StringComparer.OrdinalIgnoreCase);
        var fired = 0;

        using var sub = registry.Subscribe("GVL.Temp", (_, _) => fired++);
        registry.Fire("gvl.temp", 21.5f);

        Assert.Equal(1, fired);
    }
}
