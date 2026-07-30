namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>What changed about an alarm between two PLC snapshots.</summary>
public enum AlarmTransitionKind
{
    /// <summary>
    /// The alarm became outstanding.
    /// </summary>
    /// <remarks>
    /// <b>Every restart re-raises everything outstanding.</b> The store starts empty and
    /// ADS delivers one notification on registration, so the first snapshot after a host
    /// restart reports every alarm the PLC currently holds as newly <c>Raised</c> — the
    /// monitor has no memory across processes and cannot tell a two-day-old alarm from one
    /// that appeared while it was down. That is correct, and unavoidable without persisting
    /// the outstanding set, but it means a consumer forwarding <c>Raised</c> to a pager, a
    /// ticket queue or an SMS gateway will page for the whole outstanding set on every
    /// deployment and every crash-restart. Deduplicate downstream on
    /// <see cref="PlcAlarm.Key"/> plus <see cref="PlcAlarm.PlcTimestamp"/>, or suppress
    /// notifications for the first snapshot after start.
    /// </remarks>
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
