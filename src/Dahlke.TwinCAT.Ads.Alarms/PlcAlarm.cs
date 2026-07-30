namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// One entry of a PLC alarm array, as of the notification it was bound from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity is <see cref="Key"/>, never <see cref="EquipmentId"/>.</b> The PLC's
/// <c>sKey</c> is <c>'&lt;BMK&gt;Err&lt;Code&gt;'</c> and is unique per alarm source;
/// <c>Id</c> names the equipment and is shared by every alarm on that machine.
/// </para>
/// <para>
/// Immutable: each notification produces new instances rather than mutating the
/// previous snapshot, so a consumer holding a transition's
/// <see cref="AlarmTransition.Previous"/> keeps a stable value.
/// </para>
/// </remarks>
public sealed record PlcAlarm
{
    /// <summary>The alarm's identity, from the PLC's <c>sKey</c>.</summary>
    public required string Key { get; init; }

    /// <summary>The equipment identifier (BMK) this alarm belongs to, from the PLC's <c>Id</c>.</summary>
    public required string EquipmentId { get; init; }

    /// <summary>The error number of this alarm source, from the PLC's <c>ErrorCode</c>.</summary>
    public required uint ErrorCode { get; init; }

    /// <summary>
    /// Severity, from the PLC's <c>ErrorType</c>. A PLC value outside
    /// <see cref="AlarmSeverity"/> is cast through unchanged and logged once rather
    /// than dropped — an unrecognised severity is still a real alarm.
    /// </summary>
    public required AlarmSeverity Severity { get; init; }

    /// <summary>Whether the fault condition is currently present.</summary>
    public required bool IsActive { get; init; }

    /// <summary>Whether an operator must acknowledge this alarm.</summary>
    public required bool NeedsAcknowledgement { get; init; }

    /// <summary>Whether the alarm has been acknowledged.</summary>
    public required bool IsAcknowledged { get; init; }

    /// <summary>
    /// The PLC's timestamp for this alarm. <c>TIMESTRUCT</c> carries no time zone, so
    /// the <see cref="DateTime.Kind"/> is whatever <c>PlcClock</c> declared in
    /// configuration — <see cref="DateTimeKind.Unspecified"/> unless stated.
    /// </summary>
    public required DateTime PlcTimestamp { get; init; }

    /// <summary>
    /// The index of the array slot this alarm occupies. Slots are reused, so this is
    /// stable only while the alarm is outstanding; it exists so acknowledgement can
    /// address the entry.
    /// </summary>
    public required int SlotIndex { get; init; }

    /// <summary>The configured PLC target id this alarm was read from.</summary>
    public required string PlcId { get; init; }

    /// <summary>
    /// Human-readable text resolved from <c>IAlarmTextCatalog</c> by
    /// <see cref="Key"/>, or <see langword="null"/> when no catalog is configured or
    /// the key is absent from it.
    /// </summary>
    public string? Text { get; init; }
}
