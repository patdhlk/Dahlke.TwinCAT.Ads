using System.Globalization;
using System.Xml.Linq;

namespace Dahlke.EtherCAT.Esi;

/// <summary>
/// XML parse helpers shared by <see cref="EsiDeviceReader"/> and the per-feature parsers beside
/// it. Every one answers "what does the file state", and returns null — never a defaulted
/// stand-in — when it states nothing. That rule is what lets every nullable member on
/// <see cref="EsiDevice"/> mean exactly one thing.
/// </summary>
internal static class EsiXml
{
    /// <summary>Trimmed element text, or null when the element is absent or blank.</summary>
    public static string? Text(XElement? element)
    {
        string? trimmed = element?.Value.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>Trimmed attribute text, or null when the attribute is absent or blank.</summary>
    public static string? Text(XAttribute? attribute)
    {
        string? trimmed = attribute?.Value.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Parses an ESI hex literal (<c>#x0c843052</c> or <c>0x…</c>) to a value, or -1 when absent
    /// or unparseable. -1 can never equal a uint field, so it fails every identity comparison.
    /// Used only for identity matching, where "no answer" must not accidentally match; everything
    /// else wants <see cref="ParseNumber"/>, which reports absence as null.
    /// </summary>
    public static long ParseHex(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return -1;
        }

        string trimmed = raw.Trim();
        if (trimmed.StartsWith("#x", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return trimmed.Length > 0 &&
               long.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value)
            ? value
            : -1;
    }

    /// <summary>
    /// An ESI numeric literal: hex when prefixed <c>#x</c> or <c>0x</c>, decimal otherwise. ESI
    /// mixes the two within one element's children — a PDO entry's <c>&lt;Index&gt;</c> is
    /// <c>#x6000</c> while the <c>&lt;SubIndex&gt;</c> beside it is <c>17</c> — so a caller cannot
    /// know which form it will be handed. Null when absent or unparseable, never 0.
    /// </summary>
    public static long? ParseNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim();
        if (trimmed.StartsWith("#x", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(
                trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex)
                ? hex
                : null;
        }

        // NumberStyles.Integer admits a leading sign, which EBusCurrent needs: a coupler declares
        // its supply as a negative draw.
        return long.TryParse(
            trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long dec)
            ? dec
            : null;
    }

    /// <summary>A <see cref="ParseNumber"/> that fits an <see cref="int"/>, else null.</summary>
    public static int? ParseInt(string? raw) =>
        ParseNumber(raw) is long v and >= int.MinValue and <= int.MaxValue ? (int)v : null;

    /// <summary>A <see cref="ParseNumber"/> that fits a <see cref="ushort"/>, else null.</summary>
    public static ushort? ParseUShort(string? raw) =>
        ParseNumber(raw) is long v and >= ushort.MinValue and <= ushort.MaxValue ? (ushort)v : null;

    /// <summary>A <see cref="ParseNumber"/> that fits a <see cref="byte"/>, else null.</summary>
    public static byte? ParseByte(string? raw) =>
        ParseNumber(raw) is long v and >= byte.MinValue and <= byte.MaxValue ? (byte)v : null;

    /// <summary>
    /// An ESI boolean attribute. ESI writes these as <c>0</c>/<c>1</c> in practice and the schema
    /// also admits <c>false</c>/<c>true</c>; anything else is null rather than false, because
    /// "the file says something we do not understand" is not "the file says no".
    /// </summary>
    public static bool? ParseBool(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => null,
    };
}
