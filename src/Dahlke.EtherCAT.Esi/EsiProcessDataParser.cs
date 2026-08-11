using System.Xml.Linq;

namespace Dahlke.EtherCAT.Esi;

/// <summary>
/// Reads a device's declared process-data map out of the <c>&lt;Device&gt;</c> subtree the reader
/// has already materialised. No I/O of its own.
/// </summary>
internal static class EsiProcessDataParser
{
    /// <summary>
    /// The device's process data, or null when it declares no <c>&lt;Sm&gt;</c>,
    /// <c>&lt;TxPdo&gt;</c> and <c>&lt;RxPdo&gt;</c> at all. A device declaring any of the three
    /// returns an instance.
    /// <para>
    /// Note this is NOT the dictionary's absent-versus-empty distinction: ESI gives process data
    /// no container element — the three are direct children of <c>&lt;Device&gt;</c> — so a device
    /// declaring an empty map writes the same XML as one declaring none, and both report null.
    /// See <see cref="EsiDevice.ProcessData"/>, which states the same limitation to callers.
    /// </para>
    /// </summary>
    public static EsiProcessData? Parse(XElement device)
    {
        List<XElement> syncManagerElements = device.Elements("Sm").ToList();

        // Taken in DOCUMENT order across both element names rather than all TxPdo then all RxPdo,
        // so the reported order is the file's own.
        List<XElement> pdoElements = device.Elements()
            .Where(e => e.Name.LocalName is "TxPdo" or "RxPdo")
            .ToList();

        if (syncManagerElements.Count == 0 && pdoElements.Count == 0)
        {
            return null;
        }

        var syncManagers = new List<EsiSyncManager>(syncManagerElements.Count);
        for (int ordinal = 0; ordinal < syncManagerElements.Count; ordinal++)
        {
            XElement sm = syncManagerElements[ordinal];

            syncManagers.Add(new EsiSyncManager(
                // The ordinal is the number a PDO's Sm attribute dereferences. ESI's optional No
                // attribute wins where a vendor writes one — none does in Beckhoff's set, but
                // honouring it costs one line and removes the assumption.
                Number: EsiXml.ParseInt(EsiXml.Text(sm.Attribute("No"))) ?? ordinal,
                Name: EsiXml.Text(sm),
                StartAddress: EsiXml.ParseUShort(EsiXml.Text(sm.Attribute("StartAddress"))),
                ControlByte: EsiXml.ParseByte(EsiXml.Text(sm.Attribute("ControlByte"))),
                DefaultSize: EsiXml.ParseInt(EsiXml.Text(sm.Attribute("DefaultSize"))),
                MinSize: EsiXml.ParseInt(EsiXml.Text(sm.Attribute("MinSize"))),
                MaxSize: EsiXml.ParseInt(EsiXml.Text(sm.Attribute("MaxSize"))),
                Enabled: EsiXml.ParseBool(EsiXml.Text(sm.Attribute("Enable")))));
        }

        var pdos = new List<EsiPdo>(pdoElements.Count);
        foreach (XElement pdo in pdoElements)
        {
            // No parseable index means either malformed ESI or a PDO declared entirely by Ref into
            // a file-level <Pdos> pool (the index lives on the referenced template, not here) —
            // see the Ref remarks on EsiProcessData. Either way it is skipped rather than reported
            // at index 0.
            if (EsiXml.ParseUShort(EsiXml.Text(pdo.Element("Index"))) is not ushort index)
            {
                continue;
            }

            pdos.Add(new EsiPdo(
                Direction: pdo.Name.LocalName == "TxPdo"
                    ? EsiPdoDirection.Transmit
                    : EsiPdoDirection.Receive,
                Index: index,
                Name: EsiXml.Text(pdo.Element("Name")),
                SyncManager: EsiXml.ParseInt(EsiXml.Text(pdo.Attribute("Sm"))),
                Fixed: EsiXml.ParseBool(EsiXml.Text(pdo.Attribute("Fixed"))),
                Mandatory: EsiXml.ParseBool(EsiXml.Text(pdo.Attribute("Mandatory"))),
                Entries: Entries(pdo)));
        }

        return new EsiProcessData(pdos, syncManagers);
    }

    /// <summary>
    /// The PDO's mapped entries, in declaration order. Padding entries — index 0, a bit length
    /// and nothing else — are KEPT: dropping them would silently corrupt any consumer computing
    /// bit offsets down the PDO.
    /// </summary>
    private static IReadOnlyList<EsiPdoEntry> Entries(XElement pdo)
    {
        var entries = new List<EsiPdoEntry>();
        foreach (XElement entry in pdo.Elements("Entry"))
        {
            // An entry with no index or no length cannot be placed in the PDO's bit layout at
            // all, so it is malformed rather than merely underspecified, and is dropped. The rest
            // of the PDO is still reported: one bad entry must not cost a caller the mapping.
            if (EsiXml.ParseUShort(EsiXml.Text(entry.Element("Index"))) is not ushort index ||
                EsiXml.ParseInt(EsiXml.Text(entry.Element("BitLen"))) is not int bitLength)
            {
                continue;
            }

            entries.Add(new EsiPdoEntry(
                Index: index,
                SubIndex: EsiXml.ParseByte(EsiXml.Text(entry.Element("SubIndex"))),
                BitLength: bitLength,
                Name: EsiXml.Text(entry.Element("Name")),
                DataType: EsiXml.Text(entry.Element("DataType")),
                Comment: EsiXml.Text(entry.Element("Comment"))));
        }

        return entries;
    }
}
