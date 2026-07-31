namespace Dahlke.TwinCAT.Ads;

/// <summary>The outcome of a PLC method call made over ADS.</summary>
/// <remarks>
/// <para>
/// <b>Values here are Beckhoff's own shapes, NOT this library's neutral tree.</b> Both members
/// carry exactly what the underlying ADS client returned, undecoded. A scalar arrives as a boxed
/// primitive — an <c>INT</c> as <see cref="short"/>, a <c>DINT</c> as <see cref="int"/>, a
/// <c>STRING</c> as <see cref="string"/> — which is the only case that looks like the neutral
/// tree, and the only case verified against hardware. A struct or an array arrives as a Beckhoff
/// <c>DynamicValue</c>-family object implementing <c>TwinCAT.TypeSystem.IStructValue</c> or
/// <c>TwinCAT.TypeSystem.IArrayValue</c>: it is NOT an
/// <c>IReadOnlyDictionary&lt;string, object?&gt;</c> and NOT an <c>object?[]</c>, and casting it
/// to either throws <see cref="InvalidCastException"/>. Read a container's members through those
/// interfaces, or — if the neutral tree is what you want — read the symbol with
/// <see cref="IAdsConnection.ReadValueWithMetadataAsync"/>, which does decode.
/// </para>
/// <para>
/// This is deliberate rather than an omission. Decoding a returned container would need type
/// metadata for the method's signature, which this library does not fetch; the raw shape is what
/// the alarm package's acknowledge path consumes, and it is the shape Beckhoff itself documents.
/// </para>
/// </remarks>
/// <param name="ReturnValue">
/// The method's return value in Beckhoff's own shape — see the remarks. <see langword="null"/>
/// for a <c>VOID</c> method.
/// </param>
/// <param name="OutParameters">
/// The method's output parameters, in declaration order, each in Beckhoff's own shape — the same
/// rules as <paramref name="ReturnValue"/>, see the remarks. Positional rather than named because
/// the PLC declares them positionally and naming them would require method metadata this
/// library deliberately does not fetch.
/// </param>
public sealed record AdsRpcResult(object? ReturnValue, IReadOnlyList<object?> OutParameters);
