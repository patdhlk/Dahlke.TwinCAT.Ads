namespace Dahlke.TwinCAT.Ads;

/// <summary>One member of a PLC enumeration, as the running program declares it.</summary>
/// <param name="Name">The member's name, e.g. <c>SUCCESS</c>.</param>
/// <param name="Value">
/// The member's numeric value. <see cref="long"/> covers every realistic enum base
/// (<c>SINT</c> through <c>UDINT</c>); a <c>ULINT</c>-backed enum whose value exceeds
/// <see cref="long.MaxValue"/> is not supported and throws rather than wrapping.
/// </param>
/// <remarks>
/// Exists because PLC enum numbering is not stable across a project's life, while names are.
/// Code that maps a returned value by number is correct only against the numbering it was
/// written for; against a machine running a different one it reports a different member
/// entirely, with no error anywhere. Resolve by name.
/// </remarks>
public sealed record AdsEnumMember(string Name, long Value);
