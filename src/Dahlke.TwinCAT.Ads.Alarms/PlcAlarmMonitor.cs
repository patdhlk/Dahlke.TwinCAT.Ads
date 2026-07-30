using System.Reactive.Subjects;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// Owns one alarm-array subscription per configured target, binds each notification and
/// folds it into that target's <see cref="PlcAlarmStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>No wait for connection.</b> Subscriptions are registered at startup and are durable
/// across reconnects, so polling <c>IsConnected</c> first would only delay startup
/// without changing the outcome.
/// </para>
/// <para>
/// <b>Snapshots are serialised.</b> ADS notifications arrive on a background thread and
/// two for the same target can overlap; <see cref="ApplySnapshot"/> holds a per-target
/// lock so the diff sees a consistent previous state. Events are raised outside the lock.
/// </para>
/// </remarks>
internal sealed class PlcAlarmMonitor : IPlcAlarmMonitor, IHostedService, IDisposable
{
    private readonly IAdsConnectionPool _pool;
    private readonly IAlarmTextCatalog _catalog;
    private readonly PlcAlarmsOptions _options;
    private readonly ILogger<PlcAlarmMonitor> _logger;

    private readonly Dictionary<string, PlcAlarmStore> _stores = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Subject<AlarmTransition> _transitions = new();

    public PlcAlarmMonitor(
        IAdsConnectionPool pool,
        IAlarmTextCatalog catalog,
        IOptions<PlcAlarmsOptions> options,
        ILogger<PlcAlarmMonitor> logger)
    {
        _pool = pool;
        _catalog = catalog;
        _options = options.Value;
        _logger = logger;

        foreach (var plcId in _options.Targets.Keys)
        {
            _stores[plcId] = new PlcAlarmStore(plcId);
            _locks[plcId] = new object();
        }
    }

    /// <inheritdoc />
    public event EventHandler<AlarmTransition>? AlarmChanged;

    /// <inheritdoc />
    public IObservable<AlarmTransition> Transitions => _transitions;

    /// <inheritdoc />
    public IReadOnlyCollection<PlcAlarm> GetOutstanding() =>
        [.. _stores.Values.SelectMany(store => store.Outstanding)];

    /// <inheritdoc />
    public IReadOnlyCollection<PlcAlarm> GetOutstanding(string plcId) =>
        _stores.TryGetValue(plcId, out var store) ? store.Outstanding : [];

    /// <inheritdoc />
    public async Task<bool> AcknowledgeAsync(string plcId, string alarmKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plcId);
        ArgumentNullException.ThrowIfNull(alarmKey);

        if (!_stores.TryGetValue(plcId, out var store) ||
            !_options.Targets.TryGetValue(plcId, out var target))
        {
            return false;
        }

        var alarm = store.Outstanding.FirstOrDefault(
            a => string.Equals(a.Key, alarmKey, StringComparison.OrdinalIgnoreCase));

        if (alarm is null)
            return false;

        var connection = _pool.GetConnection(plcId);
        var slot = $"{target.SymbolPath}[{alarm.SlotIndex}]";

        // Slots are permanent and reused, so the index alone does not identify the
        // alarm. Verify the slot still holds it before writing, or an acknowledgement
        // lands on whatever alarm arrived there in the meantime. A window remains
        // between this read and the write; closing it would need a PLC-side
        // compare-and-set this contract does not offer.
        var occupant = await connection
            .ReadValueAsync<string>($"{slot}.sKey", ct)
            .ConfigureAwait(false);

        if (!string.Equals(occupant, alarm.Key, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Not acknowledging {AlarmKey} on {PlcId}: slot {Slot} now holds {Occupant}",
                alarmKey, plcId, alarm.SlotIndex, occupant);
            return false;
        }

        await connection.WriteValueAsync($"{slot}.IsAcked", true, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Acknowledged {AlarmKey} on {PlcId} (slot {Slot})", alarmKey, plcId, alarm.SlotIndex);

        return true;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (plcId, target) in _options.Targets)
        {
            var connection = _pool.GetConnection(plcId);

            var subscription = await connection.SubscribeAsync(
                target.SymbolPath,
                target.CycleTimeMs,
                (_, value) => OnNotification(plcId, target, value),
                cancellationToken).ConfigureAwait(false);

            _subscriptions.Add(subscription);

            _logger.LogInformation(
                "Monitoring alarms on {PlcId} at {SymbolPath} every {CycleTimeMs} ms",
                plcId, target.SymbolPath, target.CycleTimeMs);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
            subscription.Dispose();

        _subscriptions.Clear();
        _transitions.Dispose();
    }

    private void OnNotification(string plcId, PlcAlarmTargetOptions target, object? value)
    {
        IReadOnlyList<PlcAlarm> snapshot;

        try
        {
            snapshot = PlcAlarmBinder.Bind(value, plcId, target.SymbolPath, target.PlcClock, _logger);
        }
        catch (PlcAlarmShapeException ex)
        {
            // Loud and non-recoverable by design: a shape mismatch has no correct
            // reading, and continuing would publish a plausible but wrong alarm list.
            _logger.LogError(ex,
                "Alarm array on {PlcId} does not match the shape this package binds; alarm " +
                "monitoring for this target is not reporting valid data", plcId);
            return;
        }

        var resolved = snapshot
            .Select(alarm => alarm with { Text = _catalog.Resolve(alarm.Key) })
            .ToList();

        ApplySnapshot(plcId, resolved);
    }

    private void ApplySnapshot(string plcId, IReadOnlyList<PlcAlarm> snapshot)
    {
        IReadOnlyList<AlarmTransition> transitions;

        lock (_locks[plcId])
        {
            transitions = _stores[plcId].Apply(snapshot);
        }

        // Outside the lock: a slow or throwing subscriber must not hold up the next
        // notification for this target.
        foreach (var transition in transitions)
            Publish(transition);
    }

    private void Publish(AlarmTransition transition)
    {
        // Deliberately NOT AlarmChanged?.Invoke(...): invoking a multicast delegate stops
        // at the FIRST handler that throws, so one bad subscriber would silently starve
        // every handler registered after it. Each handler is invoked and isolated
        // separately instead, matching how the core library delivers subscription
        // callbacks. Read into a local first — a concurrent unsubscribe could otherwise
        // null the field between the test and the call.
        var handlers = AlarmChanged;

        foreach (var handler in handlers?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<AlarmTransition>)handler).Invoke(this, transition);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "An AlarmChanged handler threw for {AlarmKey} ({Kind}); delivery continues",
                    transition.Alarm.Key, transition.Kind);
            }
        }

        // A Subject cannot isolate its observers from each other the way the loop above
        // isolates event handlers — Rx's contract is that OnNext does not throw, and a
        // throwing observer skips the ones after it for THIS notification. Catching here
        // keeps that from escaping onto the notification thread and killing the
        // subscription, so the NEXT notification is still delivered.
        try
        {
            _transitions.OnNext(transition);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "A Transitions subscriber threw for {AlarmKey} ({Kind}); the subscription survives",
                transition.Alarm.Key, transition.Kind);
        }
    }
}
