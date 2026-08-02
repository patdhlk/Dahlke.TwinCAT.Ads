namespace Dahlke.TwinCAT.Ads.Testing;

/// <summary>
/// One write made by the code under test, in the order it happened.
/// </summary>
/// <param name="SymbolPath">The symbol path written.</param>
/// <param name="Value">The value written, boxed as it was stored.</param>
/// <param name="PreviousValue">
/// The value the symbol held beforehand, or <see langword="null"/> when it had none.
/// </param>
/// <param name="Changed">
/// Whether the write changed the stored value. A write of the same value is recorded with
/// <see langword="false"/> — the log records what was written, not what moved.
/// </param>
/// <remarks>
/// Harness writes made through <see cref="TestPlcTarget.Write"/> are deliberately NOT
/// recorded; see that method.
/// </remarks>
public sealed record PlcWrite(string SymbolPath, object? Value, object? PreviousValue, bool Changed);
