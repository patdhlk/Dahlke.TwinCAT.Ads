using System.Xml.Linq;

namespace Dahlke.EtherCAT.Esi;

/// <summary>
/// Reads a device's declared CoE object dictionary out of the <c>&lt;Device&gt;</c> subtree the
/// reader has already materialised. No I/O of its own.
/// </summary>
internal static class EsiDictionaryParser
{
    /// <summary>
    /// The device's dictionary, or null when it declares no <c>&lt;Profile&gt;&lt;Dictionary&gt;</c>.
    /// A declared dictionary holding no objects returns an instance with an empty
    /// <see cref="EsiObjectDictionary.Objects"/> — the two are deliberately not the same answer.
    /// </summary>
    public static EsiObjectDictionary? Parse(XElement device)
    {
        XElement? dictionary = device.Element("Profile")?.Element("Dictionary");
        if (dictionary is null)
        {
            return null;
        }

        // Sub-item STRUCTURE lives here and nowhere else — see SubItems below for why the
        // object's own <Info><SubItem> list cannot supply it.
        var dataTypes = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (XElement dataType in dictionary.Element("DataTypes")?.Elements("DataType") ?? [])
        {
            if (EsiXml.Text(dataType.Element("Name")) is string name)
            {
                // A duplicated data-type name is malformed ESI. Keeping the first and carrying on
                // beats throwing, for the same reason EsiObjectDictionary keeps the first of a
                // duplicated object index: one bad declaration must not cost a caller the whole
                // device.
                dataTypes.TryAdd(name, dataType);
            }
        }

        var objects = new List<EsiObject>();
        foreach (XElement obj in dictionary.Element("Objects")?.Elements("Object") ?? [])
        {
            // An object with no parseable index cannot be addressed by a caller and cannot be
            // matched against a live read, so it is skipped rather than reported at index 0 —
            // which would collide with a genuine 0x0000.
            if (EsiXml.ParseUShort(EsiXml.Text(obj.Element("Index"))) is not ushort index)
            {
                continue;
            }

            (EsiAccess? access, string? accessRaw, string? writeRestrictions) =
                ReadAccess(obj.Element("Flags"));

            XElement? info = obj.Element("Info");

            objects.Add(new EsiObject(
                Index: index,
                Name: EsiXml.Text(obj.Element("Name")),
                DataType: EsiXml.Text(obj.Element("Type")),
                BitSize: EsiXml.ParseInt(EsiXml.Text(obj.Element("BitSize"))),
                Access: access,
                AccessRaw: accessRaw,
                WriteRestrictions: writeRestrictions,
                DefaultData: EsiXml.Text(info?.Element("DefaultData")),
                DefaultValue: EsiXml.Text(info?.Element("DefaultValue")),
                SubItems: SubItems(obj, dataTypes)));
        }

        return new EsiObjectDictionary(objects);
    }

    /// <summary>
    /// The object's record members. Empty for a scalar object, and empty when the object's
    /// <c>&lt;Type&gt;</c> names a data type this file does not declare — which is an absence,
    /// not something to invent members for.
    /// </summary>
    /// <remarks>
    /// ESI splits a record object's declaration in two, and this is the only place that split is
    /// reconciled. <c>&lt;DataTypes&gt;&lt;DataType&gt;</c> holds the STRUCTURE — <c>SubIdx</c>,
    /// <c>Type</c>, <c>BitSize</c>, <c>BitOffs</c>, <c>Flags</c> — and the object's own
    /// <c>&lt;Info&gt;&lt;SubItem&gt;</c> list holds nothing but names and DEFAULT DATA.
    /// </remarks>
    private static IReadOnlyList<EsiObjectSubItem> SubItems(
        XElement obj, IReadOnlyDictionary<string, XElement> dataTypes)
    {
        if (EsiXml.Text(obj.Element("Type")) is not string typeName ||
            !dataTypes.TryGetValue(typeName, out XElement? dataType))
        {
            return [];
        }

        List<XElement> declared = dataType.Elements("SubItem").ToList();
        if (declared.Count == 0)
        {
            return [];
        }

        Dictionary<string, string?> defaults = DefaultDataByUniqueName(obj, declared);

        var result = new List<EsiObjectSubItem>(declared.Count);
        foreach (XElement subItem in declared)
        {
            (EsiAccess? access, string? accessRaw, string? writeRestrictions) =
                ReadAccess(subItem.Element("Flags"));

            string? name = EsiXml.Text(subItem.Element("Name"));

            result.Add(new EsiObjectSubItem(
                SubIndex: EsiXml.ParseByte(EsiXml.Text(subItem.Element("SubIdx"))),
                Name: name,
                DataType: EsiXml.Text(subItem.Element("Type")),
                BitSize: EsiXml.ParseInt(EsiXml.Text(subItem.Element("BitSize"))),
                BitOffset: EsiXml.ParseInt(EsiXml.Text(subItem.Element("BitOffs"))),
                Access: access,
                AccessRaw: accessRaw,
                WriteRestrictions: writeRestrictions,
                DefaultData: name is not null && defaults.TryGetValue(name, out string? d) ? d : null));
        }

        return result;
    }

    /// <summary>
    /// Default data from the object's own <c>&lt;Info&gt;&lt;SubItem&gt;</c> list, keyed by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>By name, never by position.</b> That list is sparse and prefix-truncated — it holds only
    /// the sub-items that carry a default. In Beckhoff's published set, 55,729 objects list a
    /// different NUMBER of sub-items than their data type declares (31,087 from truncation, 24,642
    /// from array-element expansion, where an object of an array type lists one entry per element
    /// while the type declares two), and a further 2,081 agree on count but disagree on names. A
    /// positional merge is therefore wrong for 57,810 of 165,490 objects — 35% — and it fails
    /// silently, attributing "Product code"'s default to "Revision".
    /// </para>
    /// <para>
    /// A name is accepted only when it occurs EXACTLY ONCE on both sides. Anything ambiguous is
    /// dropped, and the sub-item reports a null default: no default at all beats one that might
    /// belong to a different member.
    /// </para>
    /// </remarks>
    private static Dictionary<string, string?> DefaultDataByUniqueName(
        XElement obj, List<XElement> declared)
    {
        List<XElement> info = obj.Element("Info")?.Elements("SubItem").ToList() ?? [];
        if (info.Count == 0)
        {
            return new();
        }

        Dictionary<string, int> declaredCounts = CountNames(declared);
        Dictionary<string, int> infoCounts = CountNames(info);

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (XElement subItem in info)
        {
            if (EsiXml.Text(subItem.Element("Name")) is not string name ||
                declaredCounts.GetValueOrDefault(name) != 1 ||
                infoCounts.GetValueOrDefault(name) != 1)
            {
                continue;
            }

            result[name] = EsiXml.Text(subItem.Element("Info")?.Element("DefaultData"));
        }

        return result;
    }

    /// <summary>How many times each name occurs across <paramref name="subItems"/>.</summary>
    private static Dictionary<string, int> CountNames(IEnumerable<XElement> subItems)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (XElement subItem in subItems)
        {
            if (EsiXml.Text(subItem.Element("Name")) is string name)
            {
                counts[name] = counts.GetValueOrDefault(name) + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// The access ESI declares. The enum is null for absent text AND for text outside the
    /// schema's <c>ro</c>/<c>wo</c>/<c>rw</c>; the raw text always survives, so a vendor writing
    /// something unexpected is never reported as having stated nothing.
    /// </summary>
    private static (EsiAccess? Access, string? Raw, string? WriteRestrictions) ReadAccess(
        XElement? flags)
    {
        XElement? access = flags?.Element("Access");
        string? raw = EsiXml.Text(access);

        EsiAccess? parsed =
            raw is null ? null
            : string.Equals(raw, "ro", StringComparison.OrdinalIgnoreCase) ? EsiAccess.ReadOnly
            : string.Equals(raw, "wo", StringComparison.OrdinalIgnoreCase) ? EsiAccess.WriteOnly
            : string.Equals(raw, "rw", StringComparison.OrdinalIgnoreCase) ? EsiAccess.ReadWrite
            : null;

        return (parsed, raw, EsiXml.Text(access?.Attribute("WriteRestrictions")));
    }
}
