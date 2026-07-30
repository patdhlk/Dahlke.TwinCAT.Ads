using System.Reactive.Subjects;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// Owns one alarm-array subscription per configured target, binds each notification and
/// folds it into that target's <see cref="PlcAlarmStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>An unreachable target does not stop the host.</b> The facade's FIRST subscription
/// registration is not durable — it waits out <c>TimeoutMs</c> for a connection, throws
/// <see cref="AdsConnectionUnavailableException"/>, and the durable record is rolled back,
/// so nothing is retained for a later reconnect either. Letting that escape
/// <see cref="StartAsync"/> would fail hosted-service startup and take down monitoring of
/// every PLC that IS up. Each target is therefore registered independently and
/// concurrently: a failure is logged, the other targets are unaffected, and the failed one
/// is re-attempted when its connection next reports
/// <see cref="ConnectionState.Connected"/>. Once a target registers, its retry handler is
/// detached — from then on the facade restores the subscription across reconnects itself,
/// and a second registration would deliver every notification twice.
/// </para>
/// <para>
/// <b>Snapshots are serialised AND published in order.</b> ADS notifications arrive on a
/// background thread and two for the same target can overlap. <see cref="ApplySnapshot"/>
/// holds a per-target lock across both the diff and the publication, so a consumer folding
/// the stream into its own state can never see one target's transitions out of order. The
/// price is that a slow handler delays that target's next snapshot — which is why both
/// this type and <see cref="IPlcAlarmMonitor"/> require handlers to be quick, exactly as
/// the core library requires of subscription callbacks.
/// </para>
/// <para>
/// <b>The ordering guarantee has one hole: <see cref="Monitor"/> is reentrant.</b> A
/// handler that SYNCHRONOUSLY causes another notification for the SAME target on the SAME
/// thread re-enters <see cref="ApplySnapshot"/> through the lock it already holds, so that
/// second snapshot is applied and published in the middle of the first — which is the
/// out-of-order delivery the lock exists to prevent. Reachable only under
/// <c>SimulatedAdsConnection</c>, which fires callbacks on the writing thread, so a handler
/// that writes back to the simulated PLC can do it; real ADS delivers on a router thread,
/// where the write and the callback are never the same thread. A handler that hands work
/// off instead of doing it inline — which this type already requires — is immune either way.
/// </para>
/// </remarks>
internal sealed class PlcAlarmMonitor : IPlcAlarmMonitor, IHostedService, IDisposable
{
    private readonly IAdsConnectionPool _pool;
    private readonly IAlarmTextCatalog _catalog;
    private readonly ILogger<PlcAlarmMonitor> _logger;

    private readonly Dictionary<string, PlcAlarmTargetOptions> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlcAlarmStore> _stores = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Subject<AlarmTransition> _transitions = new();

    // Guards _disposed, _subscriptions and _retryDetach as a group. A registration can
    // complete on a pool thread at any moment — including after Dispose has returned — so
    // "am I still alive" and "record this subscription" have to be one atomic step.
    private readonly object _lifecycle = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<Action> _retryDetach = [];

    // Volatile so the notification path can read it without taking _lifecycle; the lock is
    // still what makes check-then-act atomic where that matters.
    private volatile bool _disposed;

    public PlcAlarmMonitor(
        IAdsConnectionPool pool,
        IAlarmTextCatalog catalog,
        IOptions<PlcAlarmsOptions> options,
        ILogger<PlcAlarmMonitor> logger)
    {
        _pool = pool;
        _catalog = catalog;
        _logger = logger;

        // A null Targets means "no alarm targets configured" — PlcAlarmsOptionsValidator
        // says so explicitly for code-first callers who assign the property. Agreeing with
        // the validator here keeps a legal configuration from throwing at construction.
        // Re-keyed case-insensitively so a caller-supplied dictionary with another
        // comparer cannot make target lookup disagree with the stores.
        foreach (var (plcId, target) in options.Value.Targets ?? [])
        {
            _targets[plcId] = target;
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
            !_targets.TryGetValue(plcId, out var target))
        {
            return false;
        }

        var alarm = store.Outstanding.FirstOrDefault(
            a => string.Equals(a.Key, alarmKey, StringComparison.OrdinalIgnoreCase));

        if (alarm is null)
            return false;

        var connection = _pool.GetConnection(plcId);
        var slot = $"{target.SymbolPath}[{alarm.SlotIndex}]";

        // NOTE: 'sKey' and 'IsAcked' below are PLC member names, and this is the only
        // place outside PlcAlarmBinder that spells one. They must stay in step with that
        // type's MemberKey and MemberIsAcked constants — a PLC rename caught by the binder
        // (which fails loudly, naming the member) would otherwise leave acknowledgement
        // failing separately, here, against a symbol path that no longer exists. They are
        // not shared as constants because these two are a WRITE-path contract: they are
        // appended to a symbol path for ADS, not looked up on a bound notification value.
        //
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
    /// <remarks>
    /// Targets are attempted CONCURRENTLY, not one after another. An unreachable target
    /// costs its facade's whole <c>TimeoutMs</c> before it fails (5 s by default), so a
    /// serial loop over ten offline PLCs would hold hosted-service startup for the SUM of
    /// those timeouts — the very thing <c>AdsConnectionPool.StartAsync</c> is documented to
    /// avoid. Concurrently the cost is the maximum instead. Each attempt is otherwise
    /// exactly the serial one: distinct targets touch distinct stores and locks, both
    /// dictionaries are read-only after construction, and everything mutable
    /// (<c>_subscriptions</c>, <c>_retryDetach</c>, <c>_disposed</c>) is already reached
    /// only under <c>_lifecycle</c>, because a registration could always complete on a pool
    /// thread concurrently with any of this.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Materialised before awaiting anything, so every attempt is in flight at once
        // rather than started lazily one await at a time.
        var attempts = _targets
            .Select(target => SubscribeOrArmRetryAsync(target.Key, target.Value, cancellationToken))
            .ToArray();

        var all = Task.WhenAll(attempts);

        try
        {
            await all.ConfigureAwait(false);
        }
        catch (Exception) when (all.Exception is { InnerExceptions.Count: > 1 })
        {
            // Awaiting Task.WhenAll rethrows only the FIRST fault. A non-unreachability
            // failure still has to bring the host down (see TrySubscribeAsync), and now
            // that all targets are attempted, two of them can fail at once — reporting one
            // and discarding the other would send an operator round the startup loop once
            // per misconfigured target. Matches how PlcAlarmsOptionsValidator reports every
            // misconfiguration at once. A single fault is rethrown unwrapped, unchanged
            // from the serial version.
            throw all.Exception;
        }
    }

    /// <summary>One target's startup attempt: subscribe, or arm the deferred retry.</summary>
    private async Task SubscribeOrArmRetryAsync(
        string plcId, PlcAlarmTargetOptions target, CancellationToken ct)
    {
        if (!await TrySubscribeAsync(plcId, target, ct).ConfigureAwait(false))
            ArmRetry(plcId, target);
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
        IDisposable[] subscriptions;
        Action[] detachers;

        lock (_lifecycle)
        {
            if (_disposed)
                return;

            _disposed = true;
            subscriptions = [.. _subscriptions];
            detachers = [.. _retryDetach];
            _subscriptions.Clear();
            _retryDetach.Clear();
        }

        // Outside the lock: a registration landing concurrently must be able to take
        // _lifecycle, observe _disposed and dispose its own handle.
        foreach (var detach in detachers)
            detach();

        foreach (var subscription in subscriptions)
            subscription.Dispose();

        // Complete the stream before disposing it, or every Transitions subscriber
        // observes shutdown as silence — no OnCompleted, no OnError, just a sequence that
        // stops — and an operator composing it (TakeUntil, ToTask, an await foreach over
        // ToAsyncEnumerable) waits forever for a terminal message that never comes. Reached
        // exactly once: the _disposed check above returns early on any second Dispose, so
        // this never calls OnCompleted on an already-disposed Subject (which throws
        // ObjectDisposedException).
        try
        {
            _transitions.OnCompleted();
        }
        catch (Exception ex)
        {
            // Subject delivers OnCompleted inline to each observer, and a throwing one
            // would otherwise escape Dispose — that is, escape StopAsync — leaving
            // _transitions undisposed and the shutdown reported as a monitor failure.
            _logger.LogWarning(ex,
                "A Transitions subscriber threw while the alarm monitor was completing the stream");
        }

        _transitions.Dispose();
    }

    /// <summary>
    /// Registers the alarm subscription for one target, reporting whether it is now live
    /// and retained.
    /// </summary>
    private async Task<bool> TrySubscribeAsync(
        string plcId, PlcAlarmTargetOptions target, CancellationToken ct)
    {
        IDisposable subscription;

        try
        {
            subscription = await _pool.GetConnection(plcId).SubscribeAsync(
                target.SymbolPath,
                target.CycleTimeMs,
                (_, value) => OnNotification(plcId, target, value),
                ct).ConfigureAwait(false);
        }
        catch (AdsConnectionUnavailableException ex)
        {
            // ONLY this exception. An unreachable target is an operational condition that
            // resolves itself; a bad symbol path or a cancelled startup is a fault the
            // operator has to see, and must still bring the host down — at STARTUP. On the
            // deferred retry path there is no startup left to fail, so RetryAsync's broad
            // catch logs it at Error and re-attempts on the next reconnect instead. A
            // target that was down at boot therefore never fails the host over a bad symbol
            // path; it just never registers. Said out loud in README.md, because it is the
            // one place the "a bad SymbolPath still fails startup" contract is conditional.
            _logger.LogError(ex,
                "Could not register alarm monitoring on {PlcId} at {SymbolPath}: the target is " +
                "not reachable. The host continues without it and registration is retried when " +
                "the target next connects",
                plcId, target.SymbolPath);

            return false;
        }

        lock (_lifecycle)
        {
            if (!_disposed)
            {
                _subscriptions.Add(subscription);

                _logger.LogInformation(
                    "Monitoring alarms on {PlcId} at {SymbolPath} every {CycleTimeMs} ms",
                    plcId, target.SymbolPath, target.CycleTimeMs);

                return true;
            }
        }

        // Registration completed after Dispose. Nothing holds this handle, so dispose it
        // here or it keeps firing notifications into a disposed Subject forever.
        subscription.Dispose();
        return false;
    }

    /// <summary>
    /// Arranges for <paramref name="plcId"/> to be re-attempted the next time its
    /// connection reports <see cref="ConnectionState.Connected"/>.
    /// </summary>
    private void ArmRetry(string plcId, PlcAlarmTargetOptions target)
    {
        var connection = _pool.GetConnection(plcId);

        // 0 = idle, 1 = an attempt is running. Two Connected transitions can be raised
        // concurrently from the pool's loop; only one attempt runs at a time and the
        // loser drops out rather than queueing a duplicate registration.
        var attemptInFlight = 0;

        EventHandler<ConnectionStateChangedEventArgs>? handler = null;

        void Detach() => connection.ConnectionStateChanged -= handler;

        handler = (_, args) =>
        {
            if (args.State is not ConnectionState.Connected)
                return;

            if (Interlocked.CompareExchange(ref attemptInFlight, 1, 0) != 0)
                return;

            _ = RetryAsync();
        };

        async Task RetryAsync()
        {
            try
            {
                // The host's startup token is long gone by now, and this attempt is not
                // anybody's operation to cancel.
                if (!await TrySubscribeAsync(plcId, target, CancellationToken.None).ConfigureAwait(false))
                    return;

                // Detach on success: from here the facade restores this subscription
                // across reconnects by itself, and a second registration would deliver
                // every notification twice.
                Detach();

                _logger.LogInformation(
                    "Alarm monitoring on {PlcId} at {SymbolPath} registered after the target came " +
                    "back up", plcId, target.SymbolPath);
            }
            catch (Exception ex)
            {
                // A detached task off the pool's reconnect thread — nobody awaits it, so an
                // escape here would be an unobserved task exception AND would leave the
                // gate held, wedging every future retry.
                _logger.LogError(ex,
                    "Retrying alarm-subscription registration on {PlcId} failed; it will be " +
                    "attempted again on the next reconnect", plcId);
            }
            finally
            {
                Volatile.Write(ref attemptInFlight, 0);
            }
        }

        lock (_lifecycle)
        {
            // Disposed between the failed registration and here: arming now would leave a
            // handler attached to a facade that outlives this monitor.
            if (_disposed)
                return;

            connection.ConnectionStateChanged += handler;
            _retryDetach.Add(Detach);
        }
    }

    private void OnNotification(string plcId, PlcAlarmTargetOptions target, object? value)
    {
        try
        {
            var snapshot = PlcAlarmBinder.Bind(value, plcId, target.SymbolPath, target.PlcClock, _logger);

            var resolved = snapshot
                .Select(alarm => alarm with { Text = _catalog.Resolve(alarm.Key) })
                .ToList();

            ApplySnapshot(plcId, resolved);
        }
        catch (PlcAlarmShapeException ex)
        {
            // Loud, and the whole snapshot is dropped rather than partially bound: a shape
            // mismatch has no correct reading, and continuing would publish a plausible but
            // wrong alarm list. It is NOT terminal, though — the subscription stays live, so
            // a transient malformation recovers by itself on the next good notification, and
            // the outstanding set meanwhile keeps its last good reading rather than emptying.
            // Still Error: if the PLC's type really has changed, someone must fix it.
            _logger.LogError(ex,
                "Alarm array on {PlcId} at {SymbolPath} does not match the shape this package " +
                "binds; this snapshot is dropped and the last good reading is kept. The " +
                "subscription remains active and monitoring resumes on the next notification " +
                "that binds. If this repeats, the PLC's ST_ErrorEntry no longer matches",
                plcId, target.SymbolPath);
        }
        catch (Exception ex)
        {
            // Deliberately broad, because this runs on the ADS router thread.
            // IAlarmTextCatalog.Resolve is consumer-supplied code and PlcAlarmStore.Apply
            // can throw too. The core connection would catch an escape and log it, but as
            // "a subscription callback threw" — with none of the alarm context an operator
            // needs to act on. Drop this snapshot with a diagnostic naming the target and
            // the symbol instead; the subscription stays live for the next one.
            _logger.LogError(ex,
                "Unexpected failure handling an alarm notification from {PlcId} at {SymbolPath}; " +
                "this snapshot is dropped and monitoring continues",
                plcId, target.SymbolPath);
        }
    }

    private void ApplySnapshot(string plcId, IReadOnlyList<PlcAlarm> snapshot)
    {
        // Publication happens INSIDE the lock, not after it. Releasing first lets two
        // overlapping notifications for one target publish in reverse order, so a consumer
        // folding the stream into its own state could end on Raised after Ended and show a
        // cleared alarm as live — the wrong direction for an alarm system to fail in.
        // Per-target ordering is what that buys; the cost is that a slow handler delays
        // this target's next snapshot. Other targets are unaffected — the lock is per
        // target.
        lock (_locks[plcId])
        {
            foreach (var transition in _stores[plcId].Apply(snapshot))
                Publish(transition);
        }
    }

    private void Publish(AlarmTransition transition)
    {
        // Best-effort: a notification already in flight when the host shuts down would
        // otherwise reach a disposed Subject and be reported as "a Transitions subscriber
        // threw", pointing an operator at a subscriber that did nothing wrong. Disposal can
        // still land between this check and the OnNext below, which is why that catch stays.
        if (_disposed)
            return;

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
