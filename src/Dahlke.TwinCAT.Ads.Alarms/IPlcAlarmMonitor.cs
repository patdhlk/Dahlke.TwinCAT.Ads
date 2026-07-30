namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// The alarms outstanding on every monitored PLC target, and the transitions that
/// change them.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton by <c>AddTwinCatAdsAlarms</c>. Subscriptions are opened
/// when the host starts and are durable across reconnects, inherited from the core
/// library's facade subscriptions.
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
    /// Acknowledges an outstanding alarm by writing its <c>IsAcked</c> member on the PLC.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the write was issued; <see langword="false"/> when the
    /// alarm is no longer outstanding, or its array slot no longer holds it.
    /// </returns>
    Task<bool> AcknowledgeAsync(string plcId, string alarmKey, CancellationToken ct);
}
