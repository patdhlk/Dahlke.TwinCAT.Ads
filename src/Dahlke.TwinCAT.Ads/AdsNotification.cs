namespace Dahlke.TwinCAT.Ads;

/// <summary>One value-change notification from the PLC.</summary>
/// <param name="SymbolPath">The symbol that changed.</param>
/// <param name="Value">The new value, decoded the same way as a read.</param>
/// <param name="TypeName">
/// The symbol's PLC type name, for example <c>INT</c> or <c>ST_Motor</c>. Resolved once, when the
/// subscription is registered — a symbol's type cannot change while a subscription is live.
/// </param>
/// <param name="Timestamp">
/// When the PLC recorded the change, as reported by the ADS notification itself rather than
/// measured on arrival.
/// </param>
public sealed record AdsNotification(
    string SymbolPath,
    object? Value,
    string TypeName,
    DateTimeOffset Timestamp);
