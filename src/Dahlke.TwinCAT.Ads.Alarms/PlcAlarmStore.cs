namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// Holds the outstanding alarms for ONE PLC target and turns each new snapshot
/// into the transitions that separate it from the previous one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Outstanding, not present.</b> The PLC's alarm array is fixed-size with permanent
/// slots: an alarm ends by <c>IsActive := FALSE</c>, never by leaving the array. And
/// when <c>NeedsAck</c> is set the entry outlives its fault condition until an operator
/// acknowledges it. So membership is computed, not observed —
/// <c>IsActive || (NeedsAcknowledgement &amp;&amp; !IsAcknowledged)</c>.
/// </para>
/// <para>
/// <b>Not thread-safe by itself.</b> <see cref="Apply"/> must be called from one thread
/// at a time; <c>PlcAlarmMonitor</c> serialises it under a lock. Reads of
/// <see cref="Outstanding"/> are safe from any thread — it returns an immutable snapshot
/// published by a single volatile write.
/// </para>
/// </remarks>
internal sealed class PlcAlarmStore(string plcId)
{
    private Dictionary<string, PlcAlarm> _outstanding = new(StringComparer.OrdinalIgnoreCase);

    // Reference-typed deliberately: Volatile.Read/Write constrain T to a class, so an
    // ImmutableArray<T> (a struct) could not be published this way.
    private IReadOnlyCollection<PlcAlarm> _published = Array.Empty<PlcAlarm>();

    /// <summary>The configured PLC target id this store tracks.</summary>
    public string PlcId { get; } = plcId;

    /// <summary>
    /// The alarms outstanding as of the last <see cref="Apply"/>. Safe to read from any
    /// thread; never torn.
    /// </summary>
    public IReadOnlyCollection<PlcAlarm> Outstanding => Volatile.Read(ref _published);

    /// <summary>
    /// Folds <paramref name="snapshot"/> — one whole PLC array reading — into the
    /// outstanding set and returns the transitions it produced, in snapshot order.
    /// </summary>
    public IReadOnlyList<AlarmTransition> Apply(IReadOnlyList<PlcAlarm> snapshot)
    {
        var transitions = new List<AlarmTransition>();
        var next = new Dictionary<string, PlcAlarm>(StringComparer.OrdinalIgnoreCase);

        foreach (var alarm in snapshot)
        {
            if (string.IsNullOrWhiteSpace(alarm.Key))
                continue;

            var wasTracked = _outstanding.TryGetValue(alarm.Key, out var previous);

            if (!wasTracked)
            {
                if (IsOutstanding(alarm))
                {
                    next[alarm.Key] = alarm;
                    transitions.Add(new AlarmTransition(AlarmTransitionKind.Raised, alarm, null));
                }

                continue;
            }

            // State edges first, then membership. Acknowledging an already-cleared
            // alarm produces BOTH an Acknowledged and an Ended, in that order: the
            // operator acted, and that action is what ended the alarm.
            AddStateTransitions(transitions, alarm, previous!);

            if (IsOutstanding(alarm))
                next[alarm.Key] = alarm;
            else
                transitions.Add(new AlarmTransition(AlarmTransitionKind.Ended, alarm, previous));
        }

        // A key the snapshot no longer carries at all — its slot was reused, or the PLC blanked
        // it. There is no "after" reading to report, because the alarm did not transition: it
        // stopped being described. So BOTH sides carry the last known state, which is typically
        // still IsActive or still awaiting acknowledgement — i.e. a payload that, run back
        // through IsOutstanding below, says the alarm is live. Ended is what settles that, and
        // AlarmTransitionKind.Ended documents it as the authority for exactly this reason.
        // Normalising the payload (forcing IsActive false, say) is deliberately NOT done: it
        // would publish a PlcAlarm reading the PLC never produced, and would make this Ended
        // disagree with the one emitted above, which carries a genuine snapshot.
        foreach (var (key, stale) in _outstanding)
        {
            if (!next.ContainsKey(key) && !transitions.Any(t => KeyMatches(t, key)))
                transitions.Add(new AlarmTransition(AlarmTransitionKind.Ended, stale, stale));
        }

        _outstanding = next;
        Volatile.Write(ref _published, next.Values.ToArray());
        return transitions;
    }

    /// <summary>
    /// Emits the acknowledgement and activity edges between two readings of the same
    /// alarm. Un-acknowledgement (true to false) deliberately emits nothing: it is not
    /// an acknowledgement, and inventing a transition for a state the PLC is not
    /// expected to produce is public surface we would have to keep.
    /// </summary>
    private static void AddStateTransitions(
        List<AlarmTransition> transitions, PlcAlarm alarm, PlcAlarm previous)
    {
        if (alarm.IsAcknowledged && !previous.IsAcknowledged)
            transitions.Add(new AlarmTransition(AlarmTransitionKind.Acknowledged, alarm, previous));

        if (!alarm.IsActive && previous.IsActive && IsOutstanding(alarm))
            transitions.Add(new AlarmTransition(AlarmTransitionKind.Cleared, alarm, previous));

        var faultReturned = alarm.IsActive && !previous.IsActive;
        var reFired = alarm.IsActive && previous.IsActive && alarm.PlcTimestamp > previous.PlcTimestamp;

        if (faultReturned || reFired)
            transitions.Add(new AlarmTransition(AlarmTransitionKind.Reoccurred, alarm, previous));
    }

    private static bool KeyMatches(AlarmTransition transition, string key) =>
        string.Equals(transition.Alarm.Key, key, StringComparison.OrdinalIgnoreCase);

    private static bool IsOutstanding(PlcAlarm alarm) =>
        alarm.IsActive || (alarm.NeedsAcknowledgement && !alarm.IsAcknowledged);
}
