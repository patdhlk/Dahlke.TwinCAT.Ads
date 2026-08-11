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

        var result = new List<EsiObjectSubItem>(declared.Count);
        foreach (XElement subItem in declared)
        {
            (EsiAccess? access, string? accessRaw, string? writeRestrictions) =
                ReadAccess(subItem.Element("Flags"));

            result.Add(new EsiObjectSubItem(
                SubIndex: EsiXml.ParseByte(EsiXml.Text(subItem.Element("SubIdx"))),
                Name: EsiXml.Text(subItem.Element("Name")),
                DataType: EsiXml.Text(subItem.Element("Type")),
                BitSize: EsiXml.ParseInt(EsiXml.Text(subItem.Element("BitSize"))),
                BitOffset: EsiXml.ParseInt(EsiXml.Text(subItem.Element("BitOffs"))),
                Access: access,
                AccessRaw: accessRaw,
                WriteRestrictions: writeRestrictions,
                // Task 6 replaces this with the name-join against the object's own
                // <Info><SubItem> list. Null until then, and the tests there are what force it.
                DefaultData: null));
        }

        return result;
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
