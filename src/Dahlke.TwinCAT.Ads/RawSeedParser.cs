using System.Globalization;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Parses the values of a <c>RawChannels:Seed</c> entry.
/// </summary>
/// <remarks>
/// Exposed as <see langword="internal"/> so <c>Dahlke.TwinCAT.Ads.Tests</c> (via
/// <c>InternalsVisibleTo</c>) can cover the grammar directly. Shared by
/// <see cref="TwinCatAdsOptionsValidator"/>, which rejects malformed seeds at
/// STARTUP, and by the factory, which materialises them on first use — one
/// grammar, two consumers, so a seed that validates always binds.
/// </remarks>
internal static class RawSeedParser
{
    /// <summary>
    /// Parses an index group or index offset: decimal (<c>17</c>) or
    /// <c>0x</c>-prefixed hex (<c>0x11</c>).
    /// </summary>
    /// <remarks>
    /// Both number styles are deliberately restrictive — <see cref="NumberStyles.None"/>
    /// for decimal and a bare <see cref="NumberStyles.AllowHexSpecifier"/> for hex —
    /// so a sign or surrounding whitespace is a parse FAILURE rather than something
    /// silently tolerated. An ADS index is unsigned, and <c>"-1"</c> in a
    /// configuration file is a typo with no correct reading.
    /// </remarks>
    internal static bool TryParseIndex(string text, out uint value) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(
                text.AsSpan(2), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out value)
            : uint.TryParse(
                text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <summary>Parses a hex byte payload, with or without a <c>0x</c> prefix.</summary>
    internal static bool TryParseHex(string value, out byte[] bytes, out string? error)
    {
        bytes = [];

        var text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;

        if (text.Length % 2 != 0)
        {
            error = $"Raw channel seed payload '{value}' has an odd number of hex digits.";
            return false;
        }

        var buffer = new byte[text.Length / 2];
        for (var i = 0; i < buffer.Length; i++)
        {
            if (!byte.TryParse(
                    text.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out buffer[i]))
            {
                error = $"Raw channel seed payload '{value}' is not valid hexadecimal.";
                return false;
            }
        }

        bytes = buffer;
        error = null;
        return true;
    }

    /// <summary>
    /// Six dot-separated decimal octets, each in 0-255. The single definition of a
    /// strictly well-formed AMS Net ID, shared by this parser and
    /// <see cref="AdsRawChannelFactory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately NOT delegated to <c>TwinCAT.Ads.AmsNetId.TryParse</c>.</b>
    /// That method launders an out-of-range octet instead of rejecting it:
    /// <c>AmsNetId.TryParse("999.1.1.1.1.1")</c> returns <see langword="true"/> and
    /// yields <c>0.1.1.1.1.1</c> — the octet is ZEROED, not reduced modulo 256, so
    /// <c>256</c>, <c>257</c>, <c>300</c>, <c>512</c> and <c>999</c> all collapse to
    /// the same address. Delegating would let a typo'd seed entry pass startup
    /// validation and then seed a channel the operator never named — silent data
    /// corruption rather than a parse failure. Counting six segments, as this did
    /// before, has the same hole.
    /// </para>
    /// <para>
    /// <b>One rule, two proportionate responses.</b> This parser REJECTS such an ID,
    /// because a configured seed entry is a declaration whose typo has no correct
    /// reading. <see cref="IAdsRawChannelFactory.Get"/> cannot reject — it is
    /// documented total — so it accepts the laundered ID and logs a warning
    /// instead. Both consult this method, so the two can never drift apart.
    /// </para>
    /// </remarks>
    internal static bool IsWellFormedNetId(string text)
    {
        var octets = text.Split('.');
        if (octets.Length != 6)
            return false;

        foreach (var octet in octets)
        {
            // NumberStyles.None: no sign, no whitespace, no hex — "+1", " 1" and
            // "0x1" are all malformed in an AMS Net ID.
            if (!byte.TryParse(octet, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                return false;
        }

        return true;
    }
}
