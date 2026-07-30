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
/// drop — but <see cref="RetryCount"/> does not apply to it. Its OWN bound is
/// therefore <see cref="TimeoutMs"/>; a call that triggers a transport rebuild
/// additionally waits for that rebuild's restore pass, which re-registers the
/// channel's other subscriptions sequentially under their own separate bounds.
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
    /// Simulation seed data: one entry per target, each carrying the slots to
    /// pre-load. Empty by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Binds from a <c>RawChannels:Seed</c> JSON ARRAY:
    /// </para>
    /// <code language="json">
    /// "RawChannels": {
    ///   "Mode": "Simulated",
    ///   "Seed": [
    ///     {
    ///       "AmsNetId": "192.168.1.10.3.1",
    ///       "Port": 65535,
    ///       "Slots": [
    ///         { "IndexGroup": "0x11", "IndexOffset": 1001, "Bytes": "02000000410C0000" }
    ///       ]
    ///     }
    ///   ]
    /// }
    /// </code>
    /// <para>
    /// <b>An array rather than a keyed dictionary because <c>:</c> is the
    /// configuration hierarchy separator.</b> This was once a
    /// <c>Dictionary&lt;string, Dictionary&lt;string, string&gt;&gt;</c> keyed on
    /// <c>amsNetId:port</c>; a key like <c>"1.2.3.4.5.6:851"</c> flattens into
    /// nested SECTIONS, so the port and both slot indices were swallowed into the
    /// hierarchy and the entry bound with no slots at all. No spelling of that
    /// shape could have survived <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.
    /// </para>
    /// <para>
    /// The DATA is used only when <see cref="Mode"/> is
    /// <see cref="ConnectionMode.Simulated"/>; a real channel never reads it. Every
    /// entry is nonetheless validated at startup in BOTH modes, so a malformed one
    /// left behind after switching to <see cref="ConnectionMode.Real"/> still fails
    /// the host rather than sitting silently broken until someone switches back.
    /// </para>
    /// </remarks>
    public List<AdsRawChannelSeed> Seed { get; set; } = [];

    /// <summary>
    /// Seed entries the configuration binder DISCARDED, surfaced as
    /// options-validation failures by <see cref="TwinCatAdsOptionsValidator"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is needed at all.</b> <c>ConfigurationBinder</c> reports a failed
    /// conversion differently depending on where it happens. A bad SCALAR — say
    /// <c>RawChannels:TimeoutMs = "typo"</c> — throws
    /// <see cref="InvalidOperationException"/> naming the path. A bad value inside a
    /// COLLECTION ELEMENT is swallowed and the whole element is dropped from the
    /// list, silently: <c>"Seed": [{ "AmsNetId": "1.2.3.4.5.6", "Port": "typo" }]</c>
    /// binds to an EMPTY <see cref="Seed"/> with no error of any kind. That is the
    /// same silent-seed-loss failure the array shape was adopted to eliminate, so it
    /// is detected rather than inherited.
    /// </para>
    /// <para>
    /// Internal: a channel between the binding step and the validator, not part of
    /// the configuration surface — the same arrangement
    /// <c>PlcTargetOptions.InitialValueBindingErrors</c> uses.
    /// </para>
    /// </remarks>
    internal List<string> SeedBindingErrors { get; } = [];
}

/// <summary>
/// One entry of <see cref="AdsRawChannelOptions.Seed"/>: the simulated contents of
/// a single <c>(amsNetId, port)</c> target.
/// </summary>
public sealed class AdsRawChannelSeed
{
    /// <summary>
    /// The target's AMS Net ID, as six dot-separated octets each in 0-255 — for
    /// example <c>192.168.1.10.3.1</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched against a channel on the NORMALISED Net ID, so a non-canonical
    /// spelling such as <c>01.2.3.4.5.6</c> still seeds the channel for
    /// <c>1.2.3.4.5.6</c>.
    /// </para>
    /// <para>
    /// The octet range is enforced STRICTLY at startup, which is stricter than
    /// <see cref="IAdsRawChannelFactory.Get"/>: that method accepts an out-of-range
    /// octet and resolves it the way the ADS stack does, because a lookup's only
    /// correct answer is what the wire will do. A seed entry is a declaration whose
    /// typo has no correct reading, so it is rejected instead.
    /// </para>
    /// </remarks>
    public string AmsNetId { get; set; } = string.Empty;

    /// <summary>The target's ADS port, in the range 0-65535.</summary>
    public int Port { get; set; }

    /// <summary>
    /// The slots to pre-load into this target's store. Empty by default, which
    /// seeds a reachable but empty target.
    /// </summary>
    public List<AdsRawChannelSeedSlot> Slots { get; set; } = [];
}

/// <summary>
/// One pre-loaded <c>(indexGroup, indexOffset)</c> slot of an
/// <see cref="AdsRawChannelSeed"/>.
/// </summary>
public sealed class AdsRawChannelSeedSlot
{
    /// <summary>
    /// The ADS index group. Accepts decimal (<c>17</c>) or <c>0x</c>-prefixed hex
    /// (<c>0x11</c>) — hence <see cref="string"/> rather than
    /// <see cref="uint"/>, since configuration cannot express the hex form as a
    /// number. No sign and no surrounding whitespace.
    /// </summary>
    public string IndexGroup { get; set; } = string.Empty;

    /// <summary>
    /// The ADS index offset, in the same decimal-or-<c>0x</c>-hex form as
    /// <see cref="IndexGroup"/>.
    /// </summary>
    public string IndexOffset { get; set; } = string.Empty;

    /// <summary>
    /// The slot's contents as hexadecimal, with an optional <c>0x</c> prefix and
    /// an even number of digits — <c>02000000</c> and <c>0x02FF</c> are both
    /// valid. Empty seeds a zero-length slot.
    /// </summary>
    public string Bytes { get; set; } = string.Empty;
}
