using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Dahlke.EtherCAT.Esi;

/// <summary>
/// How a CoE object or sub-item may be accessed, as ESI's <c>&lt;Flags&gt;&lt;Access&gt;</c>
/// declares it. The three members are the whole of the ESI schema's enumeration; anything else a
/// vendor writes reports as a null <c>Access</c> with the text preserved in <c>AccessRaw</c>,
/// rather than being rounded to the nearest member.
/// </summary>
public enum EsiAccess
{
    /// <summary>ESI states <c>ro</c>.</summary>
    ReadOnly,

    /// <summary>ESI states <c>wo</c>.</summary>
    WriteOnly,

    /// <summary>ESI states <c>rw</c>.</summary>
    ReadWrite,
}

/// <summary>
/// One sub-item of a record-typed CoE object — the <c>SS</c> in <c>0xIIII:SS</c>.
/// </summary>
/// <param name="SubIndex">
/// The sub-index ESI states, or null when it states none. Null is not rare and not an error:
/// 19,967 of 637,647 sub-items in Beckhoff's published set omit <c>&lt;SubIdx&gt;</c> because they
/// are members of an array type whose indices are implied by <c>&lt;ArrayInfo&gt;</c>'s
/// <c>&lt;LBound&gt;</c> and <c>&lt;Elements&gt;</c>. Deriving one from those would be inference,
/// so ESI's silence is reported as silence and such a sub-item cannot be rendered as
/// <c>0xIIII:SS</c>.
/// </param>
/// <param name="Name">The sub-item's name, e.g. <c>Vendor ID</c>.</param>
/// <param name="DataType">The name of the sub-item's data type, e.g. <c>UDINT</c>.</param>
/// <param name="BitSize">The sub-item's width in bits.</param>
/// <param name="BitOffset">The sub-item's bit offset within its parent object.</param>
/// <param name="Access">
/// The access ESI declares, or null when it declares none or something outside the schema.
/// See <paramref name="AccessRaw"/>.
/// </param>
/// <param name="AccessRaw">Whatever ESI wrote for access, verbatim.</param>
/// <param name="WriteRestrictions">
/// The <c>WriteRestrictions</c> attribute on <c>&lt;Access&gt;</c>, verbatim — <c>PreOP</c> and
/// <c>PreOP_SafeOP</c> in Beckhoff's set.
/// </param>
/// <param name="DefaultData">
/// The sub-item's default value as the raw hex byte string ESI writes, or null when the object
/// declares none for it. Not decoded into a typed value: that would mean assuming an endianness
/// and a type this library has not verified.
/// </param>
public sealed record EsiObjectSubItem(
    byte? SubIndex,
    string? Name,
    string? DataType,
    int? BitSize,
    int? BitOffset,
    EsiAccess? Access,
    string? AccessRaw,
    string? WriteRestrictions,
    string? DefaultData);

/// <summary>
/// One CoE object a device declares in its ESI file. This is the metadata a live SDO upload does
/// not carry — a read of <c>0x6000:11</c> returns bytes, not the name "Value" or the type
/// <c>INT</c>.
/// </summary>
/// <param name="Index">The object's index, e.g. <c>0x6000</c>.</param>
/// <param name="Name">The object's name, e.g. <c>RTD Inputs</c>.</param>
/// <param name="DataType">
/// The name of the object's data type. For a record object this names an ESI
/// <c>&lt;DataType&gt;</c> — e.g. <c>DT1018</c> — whose members appear in
/// <paramref name="SubItems"/>.
/// </param>
/// <param name="BitSize">The object's total width in bits.</param>
/// <param name="Access">
/// The access ESI declares, or null when it declares none or something outside the schema.
/// See <paramref name="AccessRaw"/>.
/// </param>
/// <param name="AccessRaw">Whatever ESI wrote for access, verbatim.</param>
/// <param name="WriteRestrictions">
/// The <c>WriteRestrictions</c> attribute on <c>&lt;Access&gt;</c>, verbatim.
/// </param>
/// <param name="DefaultData">
/// The object's default value as the raw hex byte string ESI writes, undecoded. Null for a record
/// object, which has no single default — its defaults are on its <paramref name="SubItems"/>.
/// </param>
/// <param name="DefaultValue">
/// The object's default in ESI's textual form, where it states one. Distinct from
/// <paramref name="DefaultData"/>: ESI carries both and they are not interchangeable.
/// </param>
/// <param name="SubItems">
/// The object's record members, nested rather than flattened. Empty for a scalar object.
/// </param>
public sealed record EsiObject(
    ushort Index,
    string? Name,
    string? DataType,
    int? BitSize,
    EsiAccess? Access,
    string? AccessRaw,
    string? WriteRestrictions,
    string? DefaultData,
    string? DefaultValue,
    IReadOnlyList<EsiObjectSubItem> SubItems);

/// <summary>
/// A device's declared CoE object dictionary — the offline complement to a live SDO read. A
/// consumer holding bytes from <c>ReadCoeObjectAsync</c> can annotate them with the authentic
/// name and type from here.
/// </summary>
/// <remarks>
/// <para>
/// A class rather than a record, unlike every other type in this namespace, for two reasons. It
/// carries behaviour (<see cref="TryGetObject"/>) rather than being a pure data carrier; and a
/// record's synthesized equality compares every declared instance field, which would include the
/// index below — so two dictionaries built from the same object list would compare UNEQUAL,
/// which is worse than having no value equality at all.
/// </para>
/// <para>
/// An instance of this type means the device declares a <c>&lt;Dictionary&gt;</c>. A device that
/// declares none reports a null <c>ObjectDictionary</c>; one that declares an empty dictionary
/// reports an instance with an empty <see cref="Objects"/>.
/// </para>
/// <para>
/// <b>Modular devices are out of scope.</b> A modular device declares its per-slot objects under
/// <c>&lt;Modules&gt;</c> rather than under <c>&lt;Device&gt;</c>, and this catalogue has no
/// concept of slot configuration. What is reported here is the device-level declaration alone,
/// which for an EJ-series modular box is not the whole dictionary.
/// </para>
/// </remarks>
public sealed class EsiObjectDictionary
{
    private readonly FrozenDictionary<ushort, EsiObject> _byIndex;

    /// <summary>Creates a dictionary over <paramref name="objects"/>, in declaration order.</summary>
    /// <param name="objects">The objects the device declares.</param>
    public EsiObjectDictionary(IReadOnlyList<EsiObject> objects)
    {
        Objects = objects;

        var map = new Dictionary<ushort, EsiObject>(objects.Count);
        foreach (EsiObject o in objects)
        {
            // TryAdd, not the indexer: a duplicated index is malformed ESI, and keeping the FIRST
            // occurrence and carrying on beats throwing. One bad object must not cost a caller the
            // whole device — the same reasoning EsiCatalog applies to one bad file in a folder.
            map.TryAdd(o.Index, o);
        }

        _byIndex = map.ToFrozenDictionary();
    }

    /// <summary>Every object the device declares, in the order the ESI file lists them.</summary>
    public IReadOnlyList<EsiObject> Objects { get; }

    /// <summary>
    /// The object declared at <paramref name="index"/>, without the caller scanning
    /// <see cref="Objects"/>. Annotating one live SDO read is the common case, so this is O(1)
    /// over a frozen index built once when the device is parsed.
    /// </summary>
    /// <param name="index">The CoE index to look up, e.g. <c>0x6000</c>.</param>
    /// <param name="obj">The object declared at that index, or null when none is.</param>
    /// <returns>True when the device declares an object at <paramref name="index"/>.</returns>
    public bool TryGetObject(ushort index, [NotNullWhen(true)] out EsiObject? obj) =>
        _byIndex.TryGetValue(index, out obj);
}
