using System.Globalization;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Parses the <c>RawChannels:Seed</c> configuration keys and payloads.
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
    /// <summary>Parses an outer seed key of the form <c>netId:port</c>.</summary>
    internal static bool TryParseChannelKey(
        string key, out string amsNetId, out int port, out string? error)
    {
        amsNetId = string.Empty;
        port = 0;

        var split = key.LastIndexOf(':');
        if (split <= 0 || split == key.Length - 1)
        {
            error = $"Raw channel seed key '{key}' must have the form 'amsNetId:port'.";
            return false;
        }

        var netIdPart = key[..split];
        if (!IsWellFormedNetId(netIdPart))
        {
            error = $"Raw channel seed key '{key}' has an AMS Net ID that is not six dot-separated octets in the range 0-255.";
            return false;
        }

        if (!TryParseNumber(key[(split + 1)..], out var parsedPort) ||
            parsedPort is < 0 or > 65535)
        {
            error = $"Raw channel seed key '{key}' has a port outside the range 0-65535.";
            return false;
        }

        amsNetId = netIdPart;
        port = (int)parsedPort;
        error = null;
        return true;
    }

    /// <summary>Parses an inner seed key of the form <c>indexGroup:indexOffset</c>.</summary>
    internal static bool TryParseSlotKey(
        string key, out uint indexGroup, out uint indexOffset, out string? error)
    {
        indexGroup = 0;
        indexOffset = 0;

        var parts = key.Split(':');
        if (parts.Length != 2)
        {
            error = $"Raw channel seed slot key '{key}' must have the form 'indexGroup:indexOffset'.";
            return false;
        }

        if (!TryParseNumber(parts[0], out var ig) || !TryParseNumber(parts[1], out var io) ||
            ig < 0 || io < 0 || ig > uint.MaxValue || io > uint.MaxValue)
        {
            error = $"Raw channel seed slot key '{key}' is not a pair of numbers (decimal or 0x-prefixed hex).";
            return false;
        }

        indexGroup = (uint)ig;
        indexOffset = (uint)io;
        error = null;
        return true;
    }

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
    /// Six dot-separated decimal octets, each in 0-255.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately NOT delegated to <c>TwinCAT.Ads.AmsNetId.TryParse</c>.</b>
    /// That method launders an out-of-range octet instead of rejecting it:
    /// <c>AmsNetId.TryParse("999.1.1.1.1.1")</c> returns <see langword="true"/> and
    /// yields <c>0.1.1.1.1.1</c>. Delegating would let a typo'd seed key pass
    /// startup validation and then seed a channel the operator never named —
    /// silent data corruption rather than a parse failure. Counting six segments,
    /// as this did before, has the same hole.
    /// </remarks>
    private static bool IsWellFormedNetId(string text)
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

    private static bool TryParseNumber(string text, out long value) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
