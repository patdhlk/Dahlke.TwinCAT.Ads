using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// The one in-memory PLC value store: thread-safe keyed slots plus THE fire
/// rule. Every simulated/in-memory data plane composes an instance of this —
/// <see cref="SimulatedAdsConnection"/> (dotted symbol paths → boxed CLR
/// values), <see cref="SimulatedRawStore"/> (index-group/offset → bytes), and
/// the test project's in-memory doubles — so the semantics of "what counts as a
/// change" and "what seeding means" exist exactly once.
/// </summary>
/// <typeparam name="TKey">Slot address — a symbol path, an (indexGroup, indexOffset) pair.</typeparam>
/// <typeparam name="TValue">Slot content — a boxed CLR value, a byte array.</typeparam>
/// <remarks>
/// <para>
/// <b>The fire rule.</b> <see cref="Write"/> returns <see langword="true"/> —
/// "this write is a change, deliver it" — for the FIRST write to a key and for
/// any write whose value differs from the stored one per the change comparer.
/// A same-value write returns <see langword="false"/>. This is on-change
/// semantics because that is what the real transports deliver: the symbol layer's
/// notifications are ADS on-change notifications, and the raw layer registers
/// <c>AdsTransMode.OnChange</c> explicitly. The store only DECIDES; delivering
/// to subscribers is the caller's step (see
/// <see cref="SubscriberRegistry{TKey, TValue}"/>), because store lifetime and
/// subscriber lifetime differ per adapter — the raw store outlives its
/// transport's subscribers deliberately.
/// </para>
/// <para>
/// <b>Seeding never signals.</b> <see cref="Seed"/> returns <see langword="void"/>
/// by design, not policy-at-the-call-site: seeding typically precedes subscriber
/// registration, and firing during setup would produce spurious initial-value
/// notifications inconsistent with real ADS behaviour. A seeded value still
/// counts as the previous value, so writing the same value later is not a change.
/// </para>
/// <para>
/// <b>Store lifetime is the owner's choice, stated at the owner.</b> This type
/// holds no lifecycle of its own. The symbol simulation owns its store per
/// connection (the store dies with it; <c>ForceReconnect</c> is a no-op for sims
/// precisely to preserve it); the raw simulation's store is owned by the factory
/// so seeded fixtures and runtime writes outlive an idle transport eviction.
/// </para>
/// <para>
/// <b>Concurrency.</b> The change decision is resolved via
/// <c>ConcurrentDictionary.AddOrUpdate</c>'s compare-and-swap retry loop: the
/// update factory may run multiple times under contention and must stay
/// side-effect-free (capture only); the captured previous value is overwritten
/// on each invocation, so after AddOrUpdate returns it holds exactly the value
/// displaced by the winning swap. The writer whose swap changed the value gets
/// <see langword="true"/>; concurrent writers arriving with the same new value
/// recapture the already-updated value and get <see langword="false"/>.
/// </para>
/// </remarks>
internal sealed class InMemoryPlcStore<TKey, TValue>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, TValue> _slots;
    private readonly IEqualityComparer<TValue> _changeComparer;

    public InMemoryPlcStore(
        IEqualityComparer<TKey>? keyComparer = null,
        IEqualityComparer<TValue>? changeComparer = null)
    {
        _slots = keyComparer is null ? new() : new(keyComparer);
        _changeComparer = changeComparer ?? EqualityComparer<TValue>.Default;
    }

    /// <summary>The stored slot keys (leaf paths for the symbol tree derivation).</summary>
    public IEnumerable<TKey> Keys => _slots.Keys;

    /// <summary>Reads the slot without any change bookkeeping.</summary>
    public bool TryRead(TKey key, [MaybeNullWhen(false)] out TValue value)
        => _slots.TryGetValue(key, out value);

    /// <summary>Stores a value without signalling. See the class remarks.</summary>
    public void Seed(TKey key, TValue value) => _slots[key] = value;

    /// <summary>
    /// Stores a value and reports whether this write is a change the caller
    /// should deliver. See the class remarks for the rule and the concurrency
    /// contract.
    /// </summary>
    public bool Write(TKey key, TValue value)
    {
        TValue? capturedPrevious = default;
        var isFirstWrite = true;
        _slots.AddOrUpdate(
            key,
            addValueFactory: _ => value,
            updateValueFactory: (_, existing) =>
            {
                capturedPrevious = existing;
                isFirstWrite = false;
                return value;
            });

        return isFirstWrite || !_changeComparer.Equals(capturedPrevious!, value);
    }
}

/// <summary>
/// Byte-content equality for raw slots: a fresh array carrying the same bytes is
/// the same value. This is what makes the raw fire rule match a real device's
/// <c>AdsTransMode.OnChange</c> notification — the device compares content, not
/// the identity of whatever buffer a writer happened to send.
/// </summary>
internal sealed class ByteSequenceEqualityComparer : IEqualityComparer<byte[]>
{
    public static ByteSequenceEqualityComparer Instance { get; } = new();

    public bool Equals(byte[]? x, byte[]? y)
        => ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

    public int GetHashCode(byte[] obj)
    {
        var hash = new HashCode();
        hash.AddBytes(obj);
        return hash.ToHashCode();
    }
}
