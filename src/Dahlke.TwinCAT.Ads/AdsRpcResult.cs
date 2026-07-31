namespace Dahlke.TwinCAT.Ads;

/// <summary>The outcome of a PLC method call made over ADS.</summary>
/// <param name="ReturnValue">
/// The method's return value, decoded to the same neutral shapes
/// <see cref="IAdsConnection.ReadValueWithMetadataAsync"/> produces — a boxed primitive for a
/// scalar, an <c>IReadOnlyDictionary&lt;string, object?&gt;</c> for a struct, an
/// <c>object?[]</c> for an array. <see langword="null"/> for a <c>VOID</c> method.
/// </param>
/// <param name="OutParameters">
/// The method's output parameters, in declaration order. Positional rather than named because
/// the PLC declares them positionally and naming them would require method metadata this
/// library deliberately does not fetch.
/// </param>
public sealed record AdsRpcResult(object? ReturnValue, IReadOnlyList<object?> OutParameters);
