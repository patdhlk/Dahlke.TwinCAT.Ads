namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Settings for the low-level raw ADS channel surface, bound from the
/// <c>RawChannels</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// Raw channels address arbitrary <c>(amsNetId, port)</c> pairs that need not
/// correspond to any configured PLC target, so their policy is global rather
/// than per-target. Callers needing a different bound for one operation pass an
/// explicit <see cref="System.TimeSpan"/> to that call.
/// </para>
/// <para>
/// <b><see cref="TimeoutMs"/> bounds each ATTEMPT, not the retry sequence.</b>
/// With the defaults a call can therefore take up to 10 seconds before throwing:
/// <see cref="RetryCount"/> of 1 permits two attempts of 5000 ms each. The worst
/// case is <c>TimeoutMs × (RetryCount + 1)</c>.
/// </para>
/// <para>
/// <see cref="IAdsRawChannel.SubscribeAsync"/> takes that same per-attempt bound
/// — for the registration itself and for each re-registration after a transport
/// drop — but <see cref="RetryCount"/> does not apply to it, so its worst case is
/// <see cref="TimeoutMs"/> flat.
/// </para>
/// </remarks>
public sealed class AdsRawChannelOptions
{
    /// <summary>
    /// Whether raw channels talk to real hardware or to the in-memory seeded
    /// store. Defaults to <see cref="ConnectionMode.Real"/>.
    /// </summary>
    /// <remarks>
    /// When this is <see cref="ConnectionMode.Real"/> the embedded AMS router is
    /// started even if every configured PLC target is simulated — raw channels
    /// have no symbol layer to fall back on and cannot route without it.
    /// <c>AddTwinCatAdsSimulation</c> forces this to
    /// <see cref="ConnectionMode.Simulated"/>.
    /// </remarks>
    public ConnectionMode Mode { get; set; } = ConnectionMode.Real;

    /// <summary>Per-attempt timeout in milliseconds. Must be greater than zero.</summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Number of RETRIES after a failed attempt (so <c>1</c> means up to two
    /// attempts). Must not be negative. Applies only to a timeout with no device
    /// answer; a device that answers with an ADS error code is never retried.
    /// </summary>
    public int RetryCount { get; set; } = 1;

    /// <summary>
    /// How long a channel may go unused before its underlying connection is
    /// disposed. Must be greater than zero. A channel with at least one live
    /// subscription is never evicted.
    /// </summary>
    public int IdleEvictionMs { get; set; } = 60_000;

    /// <summary>
    /// Simulation seed data. Outer key is <c>amsNetId:port</c>, inner key is
    /// <c>indexGroup:indexOffset</c> (decimal or <c>0x</c>-prefixed hex for
    /// each), value is a hex byte payload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The DATA is used only when <see cref="Mode"/> is
    /// <see cref="ConnectionMode.Simulated"/>; a real channel never reads it. The
    /// KEYS and payloads are nonetheless validated at startup in BOTH modes, so a
    /// malformed entry left behind after switching to
    /// <see cref="ConnectionMode.Real"/> still fails the host rather than sitting
    /// silently broken until someone switches back.
    /// </para>
    /// <para>
    /// An AMS Net ID here must be six octets each in 0-255. Note this is STRICTER
    /// than <see cref="IAdsRawChannelFactory.Get"/>, which accepts an out-of-range
    /// octet and resolves it the way the ADS stack does: a seed key is a
    /// declaration whose typo has no correct reading, whereas a lookup's only
    /// correct answer is what the wire will do.
    /// </para>
    /// </remarks>
    public Dictionary<string, Dictionary<string, string>> Seed { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
