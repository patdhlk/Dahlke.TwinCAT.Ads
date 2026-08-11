namespace Dahlke.EtherCAT.Esi;

/// <summary>
/// Which way a PDO's data travels. <b>The perspective is the SLAVE's</b>, matching the ESI
/// element names it is read from — reversing it is the classic EtherCAT confusion, so it is
/// stated here rather than left to a reader's assumption.
/// </summary>
public enum EsiPdoDirection
{
    /// <summary>
    /// <c>&lt;TxPdo&gt;</c> — the slave TRANSMITS this to the master. These are the master's
    /// process INPUTS.
    /// </summary>
    Transmit,

    /// <summary>
    /// <c>&lt;RxPdo&gt;</c> — the slave RECEIVES this from the master. These are the master's
    /// process OUTPUTS.
    /// </summary>
    Receive,
}

/// <summary>One mapped item within a PDO.</summary>
/// <param name="Index">
/// The CoE index the entry maps, e.g. <c>0x6000</c>. <c>0</c> is a real and common value: a
/// padding entry is written <c>&lt;Index&gt;#x0&lt;/Index&gt;</c> with a bit length and nothing
/// else.
/// </param>
/// <param name="SubIndex">
/// The sub-index the entry maps, or null when ESI states none — 29,681 of 694,510 entries in
/// Beckhoff's published set, padding entries among them.
/// </param>
/// <param name="BitLength">
/// The entry's width in bits. The one field here that is never null: an entry with no length
/// occupies no position in the PDO and could not be laid out at all. <c>&lt;BitLen&gt;</c> is
/// present on all 694,510 entries in Beckhoff's set; an entry lacking one is treated as malformed
/// and dropped, with the rest of its PDO still reported.
/// </param>
/// <param name="Name">The entry's name, e.g. <c>Underrange</c>. Null for padding.</param>
/// <param name="DataType">The name of the entry's data type, e.g. <c>BOOL</c>.</param>
/// <param name="Comment">ESI's free-text comment on the entry.</param>
public sealed record EsiPdoEntry(
    ushort Index,
    byte? SubIndex,
    int BitLength,
    string? Name,
    string? DataType,
    string? Comment);

/// <summary>One process-data object a device declares.</summary>
/// <param name="Direction">
/// Which way the data travels, taken from the element name — <c>&lt;TxPdo&gt;</c> or
/// <c>&lt;RxPdo&gt;</c> — rather than left for a consumer to infer from a string.
/// </param>
/// <param name="Index">The PDO's own CoE index, e.g. <c>0x1A00</c>.</param>
/// <param name="Name">The PDO's name, e.g. <c>RTD Inputs</c>.</param>
/// <param name="SyncManager">
/// The <see cref="EsiSyncManager.Number"/> this PDO is assigned to, from ESI's <c>Sm</c>
/// attribute, or null when it declares none. <b>Null is the majority case</b> — 37,541 of 58,128
/// PDOs in Beckhoff's published set carry no <c>Sm</c> — so a non-nullable field would have to
/// invent an assignment for most of them.
/// </param>
/// <param name="Fixed">
/// ESI's <c>Fixed</c> attribute: the mapping cannot be changed at runtime. Null when unstated.
/// </param>
/// <param name="Mandatory">
/// ESI's <c>Mandatory</c> attribute: the PDO cannot be deselected. Null when unstated.
/// </param>
/// <param name="Entries">
/// The PDO's mapped entries, in declaration order. <b>Padding entries are included</b>, not
/// filtered out: dropping them would silently corrupt any consumer computing bit offsets down
/// the PDO.
/// </param>
public sealed record EsiPdo(
    EsiPdoDirection Direction,
    ushort Index,
    string? Name,
    int? SyncManager,
    bool? Fixed,
    bool? Mandatory,
    IReadOnlyList<EsiPdoEntry> Entries);

/// <summary>
/// One sync manager a device declares. Note this is a sync MANAGER (ESI's <c>&lt;Sm&gt;</c>), not
/// a sync UNIT — a different concept, carried by ESI's <c>Su</c> attribute and unrelated to
/// <c>IEtherCatClient.GetSyncUnitsAsync</c> despite the similar name.
/// </summary>
/// <param name="Number">
/// The sync manager's number, which is what a PDO's <c>Sm</c> attribute dereferences. This is the
/// element's ORDINAL position among the device's <c>&lt;Sm&gt;</c> children, because that is what
/// ESI's numbering means; the schema's optional <c>No</c> attribute is preferred when a vendor
/// writes one, though none does in Beckhoff's published set.
/// </param>
/// <param name="Name">
/// The element's text — <c>MBoxOut</c>, <c>MBoxIn</c>, <c>Outputs</c>, <c>Inputs</c>.
/// </param>
/// <param name="StartAddress">The sync manager's start address in the slave's memory.</param>
/// <param name="ControlByte">The sync manager's control byte.</param>
/// <param name="DefaultSize">The default size in bytes.</param>
/// <param name="MinSize">The minimum size in bytes.</param>
/// <param name="MaxSize">The maximum size in bytes.</param>
/// <param name="Enabled">ESI's <c>Enable</c> attribute. Null when unstated.</param>
public sealed record EsiSyncManager(
    int Number,
    string? Name,
    ushort? StartAddress,
    byte? ControlByte,
    int? DefaultSize,
    int? MinSize,
    int? MaxSize,
    bool? Enabled);

/// <summary>
/// A device's declared process-data map — what it puts on and takes off the wire, and through
/// which sync manager, entirely offline.
/// </summary>
/// <remarks>
/// <b>Modular devices are out of scope.</b> A modular device declares its per-slot PDOs under
/// <c>&lt;Modules&gt;</c> rather than under <c>&lt;Device&gt;</c>, and this catalogue has no
/// concept of slot configuration. What is reported here is the device-level declaration alone,
/// which for an EJ-series modular box is not the whole process image.
/// </remarks>
/// <param name="Pdos">
/// Every PDO the device declares, in the order the file lists them — except a PDO whose
/// <c>&lt;Index&gt;</c> cannot be parsed, which is omitted rather than reported at index 0: it
/// could not be identified or matched against a live mapping anyway. One list rather than
/// separate Tx and Rx lists: each PDO carries its own <see cref="EsiPdo.Direction"/>, so it stays
/// self-describing wherever it is passed, and two lists PLUS a direction field would be redundant
/// state that can disagree. Group with
/// <c>Pdos.Where(p =&gt; p.Direction == EsiPdoDirection.Transmit)</c>.
/// </param>
/// <param name="SyncManagers">
/// The sync managers the device declares, in declaration order. A PDO's
/// <see cref="EsiPdo.SyncManager"/> refers to one by <see cref="EsiSyncManager.Number"/>.
/// </param>
public sealed record EsiProcessData(
    IReadOnlyList<EsiPdo> Pdos,
    IReadOnlyList<EsiSyncManager> SyncManagers);
