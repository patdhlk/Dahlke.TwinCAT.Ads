using System.Diagnostics.CodeAnalysis;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Provides the cached low-level <see cref="IAdsRawChannel"/> instances used to
/// address AMS targets by index group and index offset.
/// </summary>
/// <remarks>
/// <para>
/// One channel exists per <c>(amsNetId, port)</c> pair, created on demand and
/// cached for the factory's lifetime. Callers never dispose a channel; the
/// factory owns every underlying transport and releases them at host shutdown.
/// </para>
/// <para>
/// Unlike <see cref="IAdsConnectionPool"/>, targets are NOT declared in
/// configuration — a raw channel addresses whatever AMS target the caller names,
/// which is what discovery-driven use cases such as EtherCAT need.
/// </para>
/// </remarks>
public interface IAdsRawChannelFactory
{
    /// <summary>
    /// Returns the channel for <paramref name="amsNetId"/>/<paramref name="port"/>,
    /// creating it on first request.
    /// </summary>
    /// <param name="amsNetId">The target AMS Net ID, e.g. <c>"1.2.3.4.5.6"</c>.</param>
    /// <param name="port">The target AMS port.</param>
    /// <returns>
    /// The channel. Never <see langword="null"/>. Its identity is stable for the
    /// factory's lifetime, so it is safe to hold indefinitely.
    /// </returns>
    /// <remarks>
    /// <b>Total by design.</b> This never throws for a well-formed target and
    /// never blocks: reachability is discovered by operating on the channel, not
    /// by obtaining it. A channel for an unreachable target is returned happily
    /// and reports <see cref="ConnectionState.Disconnected"/>.
    /// </remarks>
    IAdsRawChannel Get(string amsNetId, int port);

    /// <summary>
    /// Attempts to retrieve the seedable store behind a simulated channel.
    /// </summary>
    /// <param name="amsNetId">The target AMS Net ID, e.g. <c>"1.2.3.4.5.6"</c>.</param>
    /// <param name="port">The target AMS port.</param>
    /// <param name="simulated">
    /// Receives the seedable store, or <see langword="null"/> when this factory is
    /// not in simulation mode.
    /// </param>
    /// <returns>
    /// <see langword="true"/> only when <see cref="AdsRawChannelOptions.Mode"/> is
    /// <see cref="ConnectionMode.Simulated"/>; otherwise <see langword="false"/>
    /// with a <see langword="null"/> out-value. Never throws.
    /// </returns>
    /// <remarks>
    /// A deliberate, narrow escape hatch for code-first seeding in tests and demo
    /// hosts, mirroring <see cref="IAdsConnectionPool.TryGetSimulatedConnection"/>.
    /// Not intended for production use.
    /// </remarks>
    bool TryGetSimulated(
        string amsNetId, int port,
        [NotNullWhen(true)] out ISimulatedRawChannel? simulated);
}
