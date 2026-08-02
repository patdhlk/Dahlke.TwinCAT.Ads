namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// A consumer of alarm transitions that cannot be attached too late to see the first one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists beside <see cref="IPlcAlarmMonitor.AlarmChanged"/>.</b> The monitor is a
/// hosted service: it registers its subscriptions during host startup, and ADS delivers a
/// notification the moment a subscription is registered — so the PLC's whole outstanding set
/// arrives within the first instants of <c>StartAsync</c>. An <c>AlarmChanged</c> handler
/// therefore has to be attached BEFORE the host starts, which DI cannot express: the consumer
/// must resolve the singleton and mutate it by hand, at the right moment, and the failure mode
/// when they do not is silent — alarms the PLC was already holding at boot simply never reach
/// them. A handler registered with <c>AddAlarmHandler</c> is resolved by the monitor as the
/// first thing it does in its own startup, before any subscription exists, so missing that
/// first snapshot is not a mistake it is possible to make. <c>AlarmChanged</c> remains for
/// consumers who want the event and will observe its ordering caveat.
/// </para>
/// <para>
/// <b>Threading, and why <c>async</c> here does not mean "returns immediately".</b>
/// <see cref="OnTransitionAsync"/> is invoked on the ADS notification thread, and the monitor
/// WAITS for the returned task before it publishes anything else. That is what buys the
/// per-target ordering guarantee — the same reason <c>AlarmChanged</c> is invoked inline — so
/// the contract is unchanged by the signature being asynchronous: <b>be quick and hand the work
/// off.</b> A handler that awaits a slow pager gateway delays that target's next snapshot, and
/// delays the <c>AlarmChanged</c> handlers and <c>Transitions</c> subscribers for this
/// transition too, because all three run on the one thread. What the <see cref="Task"/> return
/// buys is not concurrency; it is that a handler with genuinely asynchronous work no longer has
/// to write <c>async void</c> to do it.
/// </para>
/// <para>
/// <b>Implementations must be safe for concurrent use.</b> Ordering and mutual exclusion are
/// per target, not global: two PLCs deliver on their own threads under their own locks, so one
/// handler instance can be inside <see cref="OnTransitionAsync"/> twice at once for two
/// different <see cref="PlcAlarm.PlcId"/> values.
/// </para>
/// <para>
/// <b>Handlers are isolated from one another.</b> A handler that throws — or whose task faults
/// — is logged at Warning and the remaining handlers still receive that transition, matching
/// what <c>AlarmChanged</c> already promises. This is the one respect in which the asynchronous
/// signature is stronger than the event: an <c>async void</c> <c>AlarmChanged</c> handler
/// returns at its first <c>await</c>, so anything it throws after that point escapes onto the
/// thread pool where the monitor's isolation cannot reach it. A faulted <see cref="Task"/> is
/// caught wherever in the handler it came from.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// builder.Services
///     .AddTwinCatAds(builder.Configuration)
///     .AddTwinCatAdsAlarms(builder.Configuration)
///     .AddAlarmHandler&lt;PagerNotifier&gt;();
/// </code>
/// </example>
public interface IPlcAlarmHandler
{
    /// <summary>Handles one alarm state change.</summary>
    /// <param name="transition">What changed, on which target.</param>
    /// <param name="ct">
    /// Cancelled when the monitor shuts down. A handler awaiting anything that takes real time
    /// should pass this along, so a host going down is not held up for the handler's own
    /// timeout; an <see cref="OperationCanceledException"/> raised because of it is expected and
    /// is not logged as a failure.
    /// </param>
    /// <remarks>
    /// Exceptions are caught, logged and isolated — see the remarks on
    /// <see cref="IPlcAlarmHandler"/> — so throwing costs this handler that one transition and
    /// nothing else. It does not unsubscribe the handler, and the next transition is still
    /// delivered to it.
    /// </remarks>
    Task OnTransitionAsync(AlarmTransition transition, CancellationToken ct);
}
