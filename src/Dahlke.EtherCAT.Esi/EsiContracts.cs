namespace Dahlke.EtherCAT.Esi;

/// <summary>
/// The identity an ESI lookup is keyed on, taken from the slave's SCANNED identity — what is
/// physically on the bus. Never the configured identity: that is what the project expects, and
/// resolving a device description from it would describe a possibly-absent device.
/// </summary>
public readonly record struct EsiKey(uint VendorId, uint ProductCode, uint RevisionNumber);

/// <summary>
/// A device's identity as read from vendor ESI XML. Every field is nullable and every null means
/// "the ESI file does not state this" — never an empty or defaulted stand-in.
/// </summary>
public sealed record EsiDevice(
    string? VendorName,
    string? NameEn,
    string? NameDe,
    string? Group,
    string? Url);

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
