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
    /// each), value is a hex byte payload. Ignored when <see cref="Mode"/> is
    /// <see cref="ConnectionMode.Real"/>.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> Seed { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
