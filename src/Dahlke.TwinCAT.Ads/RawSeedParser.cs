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
/// <para>
/// A seed entry's AMS Net ID is NOT this class's business: that grammar is shared
/// with targets, routes and runtime channel lookups, and lives in
/// <see cref="AmsNetIdRule"/>.
/// </para>
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
}
