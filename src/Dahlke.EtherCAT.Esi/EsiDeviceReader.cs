using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Dahlke.EtherCAT.Esi;

/// <summary>
/// Streams one ESI file and returns the <c>&lt;Device&gt;</c> matching a given identity.
/// <c>&lt;Vendor&gt;</c> and <c>&lt;Groups&gt;</c> — both small and both near the top of the file —
/// are materialized whole; devices are streamed one subtree at a time, so a 36 MB family file
/// never lands in memory at once.
/// </summary>
internal static class EsiDeviceReader
{
    /// <summary>
    /// The device matching <paramref name="key"/>'s vendor ID and product code, preferring an
    /// exact <c>RevisionNo</c> match and otherwise the highest revision present. Null when this
    /// file describes no such device.
    /// </summary>
    /// <exception cref="XmlException">The file is not well-formed XML.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be opened.</exception>
    public static async Task<EsiDevice?> TryReadAsync(string filePath, EsiKey key)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Ignore,
        };

        // Created from a PATH rather than a TextReader on purpose: given a path, XmlReader detects
        // the file's declared encoding itself, which is what makes the ISO-8859-1 vendor sets
        // decode correctly. Wrapping a StreamReader here would silently assume UTF-8.
        using XmlReader reader = XmlReader.Create(filePath, settings);

        XElement? vendor = null;
        XElement? groups = null;
        XElement? best = null;
        XElement? bestVendor = null;
        XElement? bestGroups = null;
        long bestRevision = -1;

        await reader.MoveToContentAsync().ConfigureAwait(false);
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                await reader.ReadAsync().ConfigureAwait(false);
                continue;
            }

            // XNode.ReadFromAsync consumes a whole subtree and leaves the reader on the NEXT node,
            // so these branches must NOT read again. Anything else (the EtherCATInfo root,
            // Descriptions, Devices) is descended into with ReadAsync. Every branch advances the
            // reader, so the loop always terminates.
            switch (reader.LocalName)
            {
                case "Vendor":
                    vendor = (XElement)await XNode
                        .ReadFromAsync(reader, CancellationToken.None).ConfigureAwait(false);

                    // A wrong-vendor SECTION cannot contain this device, so stop streaming devices
                    // under it rather than reading every one. Not a bare null, though: an
                    // EtherCATInfoList-rooted file holds several <EtherCATInfo>/<Vendor> sections,
                    // and a later mismatched section must not discard a match `best` already holds
                    // from an earlier, correctly-matched one. Map with `bestVendor`/`bestGroups`
                    // here, NOT the `vendor`/`groups` just parsed above: those belong to THIS
                    // (mismatched) section, not the section `best` was actually found under, and
                    // attributing `best` to the wrong vendor would be exactly the kind of
                    // fabrication this catalog exists to avoid.
                    if (ParseHex(vendor.Element("Id")?.Value) != key.VendorId)
                    {
                        return best is null || bestVendor is null ? null : Map(best, bestVendor, bestGroups);
                    }

                    break;

                case "Groups":
                    groups = (XElement)await XNode
                        .ReadFromAsync(reader, CancellationToken.None).ConfigureAwait(false);
                    break;

                case "Device":
                    // Without a <Vendor> element the vendor ID cannot be confirmed, and an
                    // unconfirmed identity is not a match.
                    if (vendor is null)
                    {
                        return null;
                    }

                    var device = (XElement)await XNode
                        .ReadFromAsync(reader, CancellationToken.None).ConfigureAwait(false);

                    XElement? type = device.Element("Type");
                    if (type is null ||
                        ParseHex(type.Attribute("ProductCode")?.Value) != key.ProductCode)
                    {
                        break;
                    }

                    long revision = ParseHex(type.Attribute("RevisionNo")?.Value);
                    if (revision == key.RevisionNumber)
                    {
                        // Exact revision wins outright. Groups precedes Devices in the ESI schema,
                        // so it is already populated by now.
                        return Map(device, vendor, groups);
                    }

                    // `best is null` is its own condition, not folded into the comparison via a
                    // higher initial bestRevision: RevisionNo is optional in the ESI schema, and
                    // ParseHex returns -1 for both "absent" and "unparseable" — the same value
                    // bestRevision starts at. Without the explicit null check, a file whose only
                    // matching device omits RevisionNo would never satisfy `revision >
                    // bestRevision` (-1 > -1 is false) and would resolve to notFound instead of
                    // that device — absence indistinguishable from presence, the exact confusion
                    // this catalog exists to avoid.
                    if (best is null || revision > bestRevision)
                    {
                        bestRevision = revision;
                        best = device;
                        bestVendor = vendor;
                        bestGroups = groups;
                    }

                    break;

                default:
                    await reader.ReadAsync().ConfigureAwait(false);
                    break;
            }
        }

        // Same reasoning as the wrong-vendor return above: attribute `best` to the vendor/groups
        // captured alongside IT, not whatever `vendor`/`groups` happen to hold once the loop ends
        // (the last section streamed, which need not be the one `best` came from). `bestVendor`
        // is non-null whenever `best` is — the `vendor is null` guard above precedes every `best`
        // assignment — so the disjunct below exists only for nullable flow analysis.
        return best is null || bestVendor is null ? null : Map(best, bestVendor, bestGroups);
    }

    private static EsiDevice Map(XElement device, XElement vendor, XElement? groups) =>
        new(
            VendorName: LocalizedOrFirst(vendor, "1033"),
            NameEn: LocalizedName(device, "1033") ?? BareName(device),
            NameDe: LocalizedName(device, "1031"),
            Group: GroupName(device, groups),
            Url: Text(device.Element("URL")));

    /// <summary>
    /// The group's own name, matched from the device's <c>&lt;GroupType&gt;</c>. Null when the
    /// device declares no group, the file carries no groups, or no group matches — the last of
    /// which happens in real vendor sets and is not an error.
    /// </summary>
    private static string? GroupName(XElement device, XElement? groups)
    {
        string? groupType = Text(device.Element("GroupType"));
        if (groupType is null || groups is null)
        {
            return null;
        }

        XElement? group = groups.Elements("Group")
            .FirstOrDefault(g => string.Equals(Text(g.Element("Type")), groupType, StringComparison.Ordinal));

        return group is null ? null : LocalizedOrFirst(group, "1033");
    }

    /// <summary>The <c>LcId</c>-matched name, falling back to the first name of any locale.</summary>
    private static string? LocalizedOrFirst(XElement parent, string lcId) =>
        LocalizedName(parent, lcId) ?? Text(parent.Elements("Name").FirstOrDefault());

    private static string? LocalizedName(XElement parent, string lcId) =>
        Text(parent.Elements("Name").FirstOrDefault(n => (string?)n.Attribute("LcId") == lcId));

    /// <summary>
    /// A <c>&lt;Name&gt;</c> carrying no <c>LcId</c> at all. Used only as the ENGLISH fallback:
    /// an unlabelled name in a vendor file is conventionally English, whereas claiming it as
    /// German would be a fabrication, so <c>NameDe</c> has no equivalent fallback.
    /// </summary>
    private static string? BareName(XElement device) =>
        Text(device.Elements("Name").FirstOrDefault(n => n.Attribute("LcId") is null));

    /// <summary>Trimmed element text, or null when the element is absent or blank.</summary>
    private static string? Text(XElement? element)
    {
        string? trimmed = element?.Value.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Parses an ESI hex literal (<c>#x0c843052</c> or <c>0x…</c>) to a value, or -1 when absent
    /// or unparseable. -1 can never equal a uint field, so it fails every identity comparison.
    /// </summary>
    private static long ParseHex(string? raw)
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
}
