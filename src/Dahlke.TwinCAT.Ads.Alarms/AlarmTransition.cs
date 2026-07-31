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
    /// <remarks>
    /// <para>
    /// <b>This kind is the authority; the payload is not.</b>
    /// <see cref="AlarmTransition.Alarm"/> carries the LAST READING BEFORE the alarm ended, not
    /// a reading of the ended state — so its <see cref="PlcAlarm.IsActive"/> and
    /// <see cref="PlcAlarm.IsAcknowledged"/> describe that last reading. They are frequently
    /// still <c>IsActive == true</c>, or still awaiting acknowledgement: an alarm also ends by
    /// its slot being reused or blanked, in which case the newest thing the store ever saw is
    /// the alarm alive. Re-deriving outstanding-ness from the payload — applying
    /// <c>IsActive || (NeedsAcknowledgement &amp;&amp; !IsAcknowledged)</c> to it, as the store
    /// itself does to a snapshot — therefore concludes the alarm is live and never clears it.
    /// Trust <c>Ended</c>: it means gone, whatever the fields say.
    /// </para>
    /// <para>
    /// <b><see cref="AlarmTransition.Previous"/> can be reference-identical to
    /// <see cref="AlarmTransition.Alarm"/>.</b> On that same slot-reused/blanked path there is
    /// no "after" reading to report, so both carry the one last-known instance and a consumer
    /// diffing them sees no change at all. Do not treat an empty diff as "nothing happened" for
    /// this kind.
    /// </para>
    /// </remarks>
    Ended,
}

/// <summary>A single alarm state change.</summary>
/// <param name="Kind">What changed.</param>
/// <param name="Alarm">
/// The alarm's state after the change — EXCEPT for
/// <see cref="AlarmTransitionKind.Ended"/>, where it is the last reading before the alarm ended
/// and its <see cref="PlcAlarm.IsActive"/>/<see cref="PlcAlarm.IsAcknowledged"/> describe that
/// reading rather than the ended state. See the remarks on
/// <see cref="AlarmTransitionKind.Ended"/>.
/// </param>
/// <param name="Previous">
/// The alarm's state before the change, or <see langword="null"/> for
/// <see cref="AlarmTransitionKind.Raised"/>. For <see cref="AlarmTransitionKind.Ended"/> it may
/// be the very same instance as <paramref name="Alarm"/>.
/// </param>
public sealed record AlarmTransition(
    AlarmTransitionKind Kind,
    PlcAlarm Alarm,
    PlcAlarm? Previous);
