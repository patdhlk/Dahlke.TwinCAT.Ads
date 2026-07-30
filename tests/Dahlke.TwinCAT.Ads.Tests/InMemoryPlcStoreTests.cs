namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Unit tests for <see cref="InMemoryPlcStore{TKey, TValue}"/> — the one owner of
/// the in-memory PLC value store and its fire rule. Every simulated/in-memory
/// data plane (symbol sim, raw sim, both test fakes) composes this store, so the
/// rules are pinned here once: a write signals a change on the FIRST write to a
/// key and whenever the new value differs per the change comparer; a same-value
/// write does not signal; a seed NEVER signals but participates as the previous
/// value for later writes.
/// </summary>
public class InMemoryPlcStoreTests
{
    [Fact]
    public void Write_FirstWriteToAKey_SignalsChange()
    {
        var store = new InMemoryPlcStore<string, object?>();

        Assert.True(store.Write("MAIN.Speed", 42));
    }

    [Fact]
    public void Write_SameValueAgain_DoesNotSignal()
    {
        var store = new InMemoryPlcStore<string, object?>();
        store.Write("MAIN.Speed", 42);

        Assert.False(store.Write("MAIN.Speed", 42));
    }

    [Fact]
    public void Write_DifferentValue_Signals()
    {
        var store = new InMemoryPlcStore<string, object?>();
        store.Write("MAIN.Speed", 42);

        Assert.True(store.Write("MAIN.Speed", 43));
    }

    [Fact]
    public void Seed_NeverSignals_ButCountsAsThePreviousValue()
    {
        var store = new InMemoryPlcStore<string, object?>();

        store.Seed("MAIN.Speed", 42); // void by design: seeding cannot signal

        // The seeded value IS the previous value: writing it again is not a change.
        Assert.False(store.Write("MAIN.Speed", 42));
        Assert.True(store.Write("MAIN.Speed", 43));
    }

    [Fact]
    public void TryRead_MissingKey_IsFalse_SeededKey_IsTrue()
    {
        var store = new InMemoryPlcStore<string, object?>();

        Assert.False(store.TryRead("MAIN.Missing", out _));

        store.Seed("MAIN.Speed", 42);
        Assert.True(store.TryRead("MAIN.Speed", out var value));
        Assert.Equal(42, value);
    }

    [Fact]
    public void KeyComparer_MakesLookupsCaseInsensitive()
    {
        var store = new InMemoryPlcStore<string, object?>(StringComparer.OrdinalIgnoreCase);
        store.Write("MAIN.Speed", 42);

        Assert.True(store.TryRead("main.speed", out var value));
        Assert.Equal(42, value);
        Assert.False(store.Write("MAIN.SPEED", 42)); // same slot, same value — no change
    }

    [Fact]
    public void ChangeComparer_DecidesWhatCountsAsAChange()
    {
        // The raw adapters compare byte content, not array identity: a fresh
        // array with the same bytes is NOT a change — matching a real device's
        // OnChange notification, which is the mode the real transport registers.
        var store = new InMemoryPlcStore<(uint, uint), byte[]>(
            changeComparer: ByteSequenceEqualityComparer.Instance);

        Assert.True(store.Write((0x11, 1001), [1, 2]));
        Assert.False(store.Write((0x11, 1001), [1, 2]));
        Assert.True(store.Write((0x11, 1001), [1, 3]));
    }

    [Fact]
    public void Keys_EnumerateStoredKeys()
    {
        var store = new InMemoryPlcStore<string, object?>(StringComparer.OrdinalIgnoreCase);
        store.Seed("MAIN.A", 1);
        store.Write("MAIN.B", 2);

        Assert.Equal(["MAIN.A", "MAIN.B"], store.Keys.OrderBy(k => k));
    }
}
