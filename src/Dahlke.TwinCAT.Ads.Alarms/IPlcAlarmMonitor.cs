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
/// blocks. <see cref="AlarmChanged"/> and <see cref="Transitions"/> emit on the ADS
/// notification thread — handlers must be quick and thread-safe, and an exception
/// thrown by one is caught and logged rather than interrupting delivery to the others.
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
    event EventHandler<AlarmTransition>? AlarmChanged;

    /// <summary>
    /// The same transitions as an <see cref="IObservable{T}"/>. Hot and shared: all
    /// subscribers observe the one underlying subscription, and subscribing does not
    /// replay history.
    /// </summary>
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
