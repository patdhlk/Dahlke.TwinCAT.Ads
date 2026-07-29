using System.Diagnostics.CodeAnalysis;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Provides the cached low-level <see cref="IAdsRawChannel"/> instances used to
/// address AMS targets by index group and index offset.
/// </summary>
/// <remarks>
/// <para>
/// One channel exists per <c>(amsNetId, port)</c> pair — with the Net ID
/// normalised as <see cref="Get"/> describes — created on demand and cached for
/// the factory's lifetime. Callers never dispose a channel; the factory owns
/// every underlying transport and releases them at host shutdown, after which
/// operating on any channel fails fast rather than opening a new one.
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
    /// <param name="amsNetId">
    /// The target AMS Net ID, e.g. <c>"1.2.3.4.5.6"</c>. Normalised before lookup
    /// — see the remarks.
    /// </param>
    /// <param name="port">The target AMS port.</param>
    /// <returns>
    /// The channel. Never <see langword="null"/>. Its identity is stable for the
    /// factory's lifetime, so it is safe to hold indefinitely.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="amsNetId"/> is <see langword="null"/>. This is the only
    /// input that throws — see the remarks on totality.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Total by design.</b> This never throws for any present Net ID and never
    /// blocks: reachability is discovered by operating on the channel, not by
    /// obtaining it. A channel for an unreachable target — or for a malformed,
    /// empty or whitespace-only Net ID — is returned happily and reports
    /// <see cref="ConnectionState.Disconnected"/> until an operation says
    /// otherwise. The sole exception is a <see langword="null"/> argument, which
    /// is a caller programming error rather than a target that happens not to
    /// exist, and throws <see cref="ArgumentNullException"/>.
    /// </para>
    /// <para>
    /// <b>The Net ID is normalised, so one device is one channel.</b> The value is
    /// trimmed and canonicalised before it is used as a key, so
    /// <c>"1.2.3.4.5.6"</c>, <c>"01.2.3.4.5.6"</c> and <c>" 1.2.3.4.5.6"</c> all
    /// return the SAME channel — and, in simulation, share one seedable store, so
    /// a seed applied through one spelling is visible through another. The port
    /// is matched exactly; only the Net ID is normalised. A Net ID that cannot be
    /// parsed at all is used as-is (trimmed) rather than rejected.
    /// </para>
    /// <para>
    /// <b>An octet outside 0-255 is zeroed, not rejected — and that changes which
    /// device you address.</b> <c>Get("999.1.1.1.1.1", 851)</c> returns the channel
    /// for <c>0.1.1.1.1.1</c>, and a warning is logged once for each distinct
    /// spelling the caller writes — on whichever lookup first uses that spelling,
    /// whether or not the channel already existed. This is not a quirk of this
    /// method: the ADS stack resolves the
    /// address the same way when the transport connects, so the channel really does
    /// reach the device the key names. Note the deliberate asymmetry with
    /// configuration — the identical Net ID in a
    /// <see cref="AdsRawChannelOptions.Seed"/> key FAILS the host at startup,
    /// because a seed key is a declaration whose typo has no correct reading,
    /// whereas this is a lookup whose only correct answer is what the wire will do.
    /// </para>
    /// <para>
    /// <b>After the host has shut down this still returns a channel, but operating
    /// on it fails fast</b> with <see cref="AdsConnectionUnavailableException"/>
    /// rather than opening a transport nothing would ever dispose. This mirrors
    /// <see cref="IAdsConnection"/>'s rule for a stopped pool: a transport will
    /// never be published again, so waiting would only delay shutdown.
    /// </para>
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
    /// with a <see langword="null"/> out-value.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="amsNetId"/> is <see langword="null"/>. No other input
    /// throws: like <see cref="Get"/>, this answers for any present Net ID,
    /// however malformed, and normalises it the same way.
    /// </exception>
    /// <remarks>
    /// A deliberate, narrow escape hatch for code-first seeding in tests and demo
    /// hosts, mirroring <see cref="IAdsConnectionPool.TryGetSimulatedConnection"/>.
    /// Not intended for production use.
    /// </remarks>
    bool TryGetSimulated(
        string amsNetId, int port,
        [NotNullWhen(true)] out ISimulatedRawChannel? simulated);
}
