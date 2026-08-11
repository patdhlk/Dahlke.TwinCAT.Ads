namespace Dahlke.EtherCAT.Esi;

/// <summary>
/// The identity an ESI lookup is keyed on, taken from the slave's SCANNED identity — what is
/// physically on the bus. Never the configured identity: that is what the project expects, and
/// resolving a device description from it would describe a possibly-absent device.
/// </summary>
public readonly record struct EsiKey(uint VendorId, uint ProductCode, uint RevisionNumber);

/// <summary>
/// A device's description as read from vendor ESI XML. Every field is nullable and every null
/// means "the ESI file does not state this" — never an empty or defaulted stand-in.
/// </summary>
/// <param name="VendorName">The vendor's own name, English where the file offers a choice.</param>
/// <param name="NameEn">The device's English name, or an unlabelled name where there is one.</param>
/// <param name="NameDe">The device's German name. No unlabelled fallback: claiming an
/// unlabelled name as German would be a fabrication.</param>
/// <param name="Group">The device group's name, resolved from the device's group type.</param>
/// <param name="Url">The vendor's URL for the device.</param>
/// <param name="EBusCurrentMa">
/// The E-bus current the device declares, in mA, from <c>&lt;Info&gt;&lt;Electrical&gt;</c>.
/// <para>
/// <b>Sign is ESI's own convention: positive DRAWS from the E-bus, negative SUPPLIES it.</b> An
/// EL3201 terminal declares <c>190</c>; an EK1100 coupler declares <c>-500</c>, and its own name
/// reads "EK1100 EtherCAT Coupler (0.5A E-Bus)". ESI does not distinguish the two structurally —
/// it is a single signed integer — so there is nothing to model separately.
/// </para>
/// <para>
/// Null when the file declares no <c>&lt;EBusCurrent&gt;</c>, and <b>never 0 for that case</b>:
/// 478 devices in Beckhoff's published set declare a genuine <c>0</c>, so a consumer summing
/// draws across a segment must be able to tell an unknown contributor from one that draws
/// nothing. Text that cannot be parsed as a number is also null, which does conflate "states
/// something unreadable" with "states nothing"; no such device exists in Beckhoff's 868 MB set,
/// so a third state would be API surface with no reader for it.
/// </para>
/// <para>
/// Aggregating these into a per-segment load against a segment budget is the consumer's job.
/// This library reports the per-device figure.
/// </para>
/// </param>
/// <param name="ObjectDictionary">
/// The CoE object dictionary the device declares, or null when it declares none. A device that
/// declares an EMPTY dictionary reports a non-null value whose <c>Objects</c> is empty — the two
/// are deliberately different answers.
/// </param>
/// <param name="ProcessData">
/// The process-data map the device declares — its PDOs and sync managers — or null when it
/// declares none. <b>Unlike <see cref="ObjectDictionary"/>, ESI gives process data no container
/// element</b> — <c>&lt;Sm&gt;</c>, <c>&lt;TxPdo&gt;</c> and <c>&lt;RxPdo&gt;</c> are direct
/// children of <c>&lt;Device&gt;</c> — so a device with no sync managers and no PDOs is
/// indistinguishable from one declaring an empty map, and both report null.
/// </param>
public sealed record EsiDevice(
    string? VendorName,
    string? NameEn,
    string? NameDe,
    string? Group,
    string? Url,
    int? EBusCurrentMa,
    EsiObjectDictionary? ObjectDictionary,
    EsiProcessData? ProcessData);

/// <summary>Why an ESI lookup produced a device, or why it did not.</summary>
public enum EsiStatus
{
    /// <summary>The device was found; its description is populated from ESI XML.</summary>
    Resolved,

    /// <summary>No ESI directory is configured, or the configured one does not exist.</summary>
    NotConfigured,

    /// <summary>
    /// The slave's scanned identity is unavailable, so there was nothing to look up — either the
    /// slave was never in the scan at all, or it was scanned but its own identity read did not
    /// answer.
    /// </summary>
    IdentityUnavailable,

    /// <summary>The identity is absent from the ESI set — or the search hit its budget first.</summary>
    NotFound,

    /// <summary>An ESI file could not be read or parsed.</summary>
    ReadFailed,
}

/// <summary>
/// Outcome of one ESI lookup — either a resolved device, or a status explaining why one could not
/// be produced. The four static fields below are the non-<see cref="EsiStatus.Resolved"/> results
/// and always carry a null <paramref name="Device"/>.
/// </summary>
/// <param name="Device">
/// The resolved device description, or null when <paramref name="Status"/> is anything but
/// <see cref="EsiStatus.Resolved"/>.
/// </param>
/// <param name="Status">Why the lookup produced <paramref name="Device"/>, or why it did not.</param>
public sealed record EsiLookupResult(EsiDevice? Device, EsiStatus Status)
{
    /// <summary>No ESI directory is configured, or the configured one does not exist.</summary>
    public static readonly EsiLookupResult NotConfigured = new(null, EsiStatus.NotConfigured);

    /// <summary>
    /// The slave's scanned identity is unavailable, so there was nothing to look up.
    /// </summary>
    public static readonly EsiLookupResult IdentityUnavailable = new(null, EsiStatus.IdentityUnavailable);

    /// <summary>The identity is absent from the ESI set, or the search hit its budget first.</summary>
    public static readonly EsiLookupResult NotFound = new(null, EsiStatus.NotFound);

    /// <summary>An ESI file could not be read or parsed.</summary>
    public static readonly EsiLookupResult ReadFailed = new(null, EsiStatus.ReadFailed);
}

/// <summary>Resolves a scanned slave's identity to its ESI device description.</summary>
public interface IEsiCatalog
{
    /// <summary>
    /// Resolves <paramref name="key"/> to its ESI description. <paramref name="typeHint"/> only
    /// orders the file search and may be useless without affecting correctness.
    /// </summary>
    /// <remarks>
    /// Takes no <see cref="CancellationToken"/> deliberately — see the spec. Results are cached
    /// per key, and a cancelled task cached under a key would be inherited by every later caller.
    /// Boundedness comes from <c>EtherCat:Esi:LookupBudgetMs</c> instead.
    /// </remarks>
    Task<EsiLookupResult> LookupAsync(EsiKey key, string typeHint);
}
