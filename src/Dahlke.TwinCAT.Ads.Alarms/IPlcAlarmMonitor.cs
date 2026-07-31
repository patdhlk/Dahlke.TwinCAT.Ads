namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// The alarms outstanding on every monitored PLC target, and the transitions that
/// change them.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton by <c>AddTwinCatAdsAlarms</c>. Subscriptions are opened
/// when the host starts and are durable across reconnects, inherited from the core
/// library's facade subscriptions. A target that is unreachable at startup does NOT
/// prevent the host from starting: it is logged, the other targets are monitored
/// normally, and the unreachable one is registered automatically once it connects.
/// Until then it reports no alarms.
/// </para>
/// <para>
/// <b>Ordering.</b> Transitions for one target are delivered in the order they were
/// computed, so a consumer folding the stream into its own state never sees, say, a
/// <c>Raised</c> arrive after the <c>Ended</c> that followed it. This is guaranteed per
/// target, not across targets. It is achieved by holding that target's lock across
/// delivery, so a handler that blocks delays the NEXT snapshot for that target — one
/// more reason the handlers below must be quick.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="GetOutstanding()"/> is safe from any thread and never
/// blocks. <see cref="AlarmChanged"/> and <see cref="Transitions"/> both emit on the ADS
/// notification thread, so handlers and subscribers must be quick and thread-safe.
/// </para>
/// <para>
/// <b>A throwing consumer is treated differently on the two paths, deliberately.</b>
/// <see cref="AlarmChanged"/> handlers ARE isolated from one another: each is invoked
/// separately, and an exception from one is caught and logged without stopping the
/// others from receiving that transition. <see cref="Transitions"/> subscribers are NOT
/// isolated — they follow the standard Rx contract, under which throwing from
/// <c>OnNext</c> is a bug in the observer, so a subscriber that throws can prevent
/// subscribers after it from seeing that transition. The asymmetry is intentional and
/// should not be "fixed": an observable that silently swallowed observer exceptions
/// would behave unlike every other one an Rx consumer composes with. What both paths do
/// guarantee is that the exception never escapes onto the notification thread — the
/// subscription survives either way, and the NEXT transition is still delivered.
/// </para>
/// </remarks>
public interface IPlcAlarmMonitor
{
    /// <summary>The outstanding alarms across every monitored target.</summary>
    IReadOnlyCollection<PlcAlarm> GetOutstanding();

    /// <summary>
    /// The outstanding alarms on one target. An unmonitored or unknown
    /// <paramref name="plcId"/> yields an empty collection rather than throwing —
    /// callers polling a dashboard should not have to distinguish "no alarms" from
    /// "not monitored" mid-render.
    /// </summary>
    IReadOnlyCollection<PlcAlarm> GetOutstanding(string plcId);

    /// <summary>Raised for every alarm state change.</summary>
    /// <remarks>
    /// Handlers are isolated from one another — a handler that throws is logged and does
    /// not stop delivery to the handlers registered after it. See the type-level
    /// threading remarks for how this differs from <see cref="Transitions"/>.
    /// </remarks>
    event EventHandler<AlarmTransition>? AlarmChanged;

    /// <summary>
    /// The same transitions as an <see cref="IObservable{T}"/>. Hot and shared: all
    /// subscribers observe the one underlying subscription, and subscribing does not
    /// replay history.
    /// </summary>
    /// <remarks>
    /// Standard Rx semantics apply: do not throw from <c>OnNext</c>. Unlike
    /// <see cref="AlarmChanged"/>, subscribers here are NOT isolated from one another.
    /// See the type-level threading remarks.
    /// </remarks>
    IObservable<AlarmTransition> Transitions { get; }

    /// <summary>
    /// Acknowledges an outstanding alarm on the PLC, by <see cref="PlcAlarm.Key"/>, through
    /// the registered <see cref="IPlcAlarmDialect"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the PLC acknowledged it; <see langword="false"/> when the
    /// target is not monitored, the alarm is not outstanding here, or the PLC has nothing by
    /// that key to acknowledge.
    /// </returns>
    /// <remarks>
    /// A <see langword="false"/> result means "there was nothing to acknowledge", never
    /// "something went wrong on the PLC": a refusal for any other reason raises
    /// <see cref="PlcAlarmAcknowledgeException"/>, so a caller is never left unable to tell
    /// "it is gone" from "try again".
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plcId"/> or <paramref name="alarmKey"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="PlcAlarmAcknowledgeException">
    /// The PLC refused the acknowledgement for a reason other than "no such alarm".
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled before the dialect finished.
    /// </exception>
    /// <exception cref="global::TwinCAT.Ads.AdsErrorException">
    /// The PLC rejected what the dialect asked of it — for the shipped dialect, most often an
    /// acknowledging method that is not reachable over ADS.
    /// </exception>
    /// <exception cref="AdsConnectionUnavailableException">
    /// The target's connection was unavailable for longer than its configured timeout.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// The per-target timeout elapsed before the acknowledgement completed.
    /// </exception>
    Task<bool> AcknowledgeAsync(string plcId, string alarmKey, CancellationToken ct);
}
