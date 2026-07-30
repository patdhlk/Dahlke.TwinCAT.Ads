namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// The durable simulated state for one <c>(amsNetId, port)</c> target: the byte
/// slots, and the seeding entry point consumers reach through
/// <see cref="IAdsRawChannelFactory.TryGetSimulated"/>.
/// </summary>
/// <remarks>
/// <para>
/// This exists so a simulated transport can be a FRESH instance on every
/// <see cref="ManagedRawConnectionFactory"/> call while seeded fixtures and
/// runtime writes still outlive an idle eviction. The factory owns one store per
/// target; each <see cref="SimulatedRawConnection"/> wraps it. The slots
/// themselves are an <see cref="InMemoryPlcStore{TKey, TValue}"/> comparing byte
/// CONTENT for the fire rule — the shared module that also backs the symbol
/// simulation, so "what counts as a change" is one implementation.
/// </para>
/// <para>
/// <b>Subscriptions deliberately do NOT live here.</b> They stay per-connection,
/// mirroring reality: disposing a real <c>AdsClient</c> drops its notification
/// registrations, and <see cref="AdsRawChannel"/> re-registers them against the
/// replacement transport. Keeping them in the store would mean a dropped
/// transport silently kept delivering.
/// </para>
/// </remarks>
internal sealed class SimulatedRawStore : ISimulatedRawChannel
{
    /// <summary>The seeded and written byte slots, keyed by index group and offset.</summary>
    internal InMemoryPlcStore<(uint IndexGroup, uint IndexOffset), byte[]> Slots { get; } =
        new(changeComparer: ByteSequenceEqualityComparer.Instance);

    public void Seed(uint indexGroup, uint indexOffset, ReadOnlySpan<byte> data) =>
        Slots.Seed((indexGroup, indexOffset), data.ToArray());
}
