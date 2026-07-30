namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>What changed about an alarm between two PLC snapshots.</summary>
public enum AlarmTransitionKind
{
    /// <summary>The alarm became outstanding.</summary>
    Raised,

    /// <summary>The alarm was acknowledged (false to true).</summary>
    Acknowledged,

    /// <summary>
    /// The fault condition ended while the alarm remained outstanding, because it
    /// still awaits acknowledgement.
    /// </summary>
    Cleared,

    /// <summary>
    /// The fault condition returned, or the PLC timestamp advanced while the alarm
    /// was already active.
    /// </summary>
    Reoccurred,

    /// <summary>The alarm is no longer outstanding and has left the store.</summary>
    Ended,
}

/// <summary>A single alarm state change.</summary>
/// <param name="Kind">What changed.</param>
/// <param name="Alarm">The alarm's state after the change.</param>
/// <param name="Previous">
/// The alarm's state before the change, or <see langword="null"/> for
/// <see cref="AlarmTransitionKind.Raised"/>.
/// </param>
public sealed record AlarmTransition(
    AlarmTransitionKind Kind,
    PlcAlarm Alarm,
    PlcAlarm? Previous);
