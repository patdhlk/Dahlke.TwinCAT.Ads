namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Test-support API for seeding a simulated raw channel's byte store.
/// </summary>
/// <remarks>
/// <para>
/// A deliberate, narrow escape hatch for code-first seeding and inspection,
/// mirroring <see cref="IAdsConnectionPool.TryGetSimulatedConnection"/>. Reach it
/// via <see cref="IAdsRawChannelFactory.TryGetSimulated"/>, which returns
/// <see langword="false"/> for a real channel rather than throwing.
/// </para>
/// <para>
/// <b>The store carries no protocol knowledge.</b> It does not know what an index
/// group means, what a CoE object is, or how the file protocol frames a request.
/// A consumer seeds the exact bytes it expects its own decoder to read back.
/// </para>
/// </remarks>
public interface ISimulatedRawChannel
{
    /// <summary>
    /// Seeds the slot at <paramref name="indexGroup"/>/<paramref name="indexOffset"/>,
    /// replacing any existing content.
    /// </summary>
    void Seed(uint indexGroup, uint indexOffset, ReadOnlySpan<byte> data);
}
