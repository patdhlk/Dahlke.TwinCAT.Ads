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
/// at a time; <see cref="PlcAlarmMonitor"/> serialises it under a lock. Reads of
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
            // An unoccupied slot in a fixed array carries a blank key.
            if (string.IsNullOrWhiteSpace(alarm.Key))
                continue;

            var wasTracked = _outstanding.TryGetValue(alarm.Key, out var previous);

            if (!IsOutstanding(alarm))
            {
                if (wasTracked)
                    transitions.Add(new AlarmTransition(AlarmTransitionKind.Ended, alarm, previous));
                continue;
            }

            next[alarm.Key] = alarm;

            if (!wasTracked)
                transitions.Add(new AlarmTransition(AlarmTransitionKind.Raised, alarm, null));
        }

        _outstanding = next;
        Volatile.Write(ref _published, next.Values.ToArray());
        return transitions;
    }

    private static bool IsOutstanding(PlcAlarm alarm) =>
        alarm.IsActive || (alarm.NeedsAcknowledgement && !alarm.IsAcknowledged);
}
