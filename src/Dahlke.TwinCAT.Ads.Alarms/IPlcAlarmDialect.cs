namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>Everything this package needs to know about one PLC's alarm implementation.</summary>
/// <remarks>
/// <para>
/// Binding an alarm entry and acknowledging it are one vendor's concern, not two: on the
/// reference PLC the alarm array is a read-only projection rebuilt every scan, which is
/// precisely why acknowledgement is a method call rather than a write. Splitting the two would
/// let a caller pair a binder with an incompatible acknowledger, and the failure would look
/// exactly like the defect this seam replaces — an acknowledge that reports success and
/// changes nothing.
/// </para>
/// <para>
/// Register your own before calling <c>AddTwinCatAdsAlarms</c> to replace the built-in
/// implementation. Implementations must be safe for concurrent use: <see cref="Bind"/> runs on
/// the ADS notification thread.
/// </para>
/// </remarks>
public interface IPlcAlarmDialect
{
    /// <summary>Binds one alarm-array notification into alarms.</summary>
    /// <remarks>
    /// Must fail loudly. A shape it does not recognise raises
    /// <see cref="PlcAlarmShapeException"/>; it must never substitute defaults for members it
    /// cannot read, because a plausible-looking wrong alarm list is worse than none.
    /// </remarks>
    IReadOnlyList<PlcAlarm> Bind(AlarmBindContext context);

    /// <summary>Acknowledges one alarm on the PLC.</summary>
    /// <returns>
    /// <see langword="true"/> when the PLC acknowledged it; <see langword="false"/> when the
    /// alarm is not there to acknowledge. Those are the only two outcomes this returns —
    /// a PLC that refuses for any other reason throws, so a caller is never left unable to
    /// distinguish "it is gone" from "try again".
    /// </returns>
    /// <exception cref="PlcAlarmAcknowledgeException">The PLC refused the acknowledgement.</exception>
    Task<bool> AcknowledgeAsync(AlarmAcknowledgeContext context, CancellationToken ct);
}

/// <summary>The inputs to <see cref="IPlcAlarmDialect.Bind"/>.</summary>
/// <remarks>
/// A record rather than loose parameters so a future input can be added without breaking
/// every implementation of the interface.
/// </remarks>
/// <param name="NotificationValue">The raw value the ADS notification carried.</param>
/// <param name="PlcId">The configured target id the notification came from.</param>
/// <param name="SymbolPath">The alarm array's symbol path.</param>
/// <param name="PlcClock">What clock the PLC's timestamps are expressed in.</param>
/// <param name="Logger">For diagnostics; may be <see langword="null"/>.</param>
public sealed record AlarmBindContext(
    object? NotificationValue,
    string PlcId,
    string SymbolPath,
    PlcClockKind PlcClock,
    ILogger? Logger);

/// <summary>The inputs to <see cref="IPlcAlarmDialect.AcknowledgeAsync"/>.</summary>
/// <param name="Alarm">The alarm to acknowledge, as last bound.</param>
/// <param name="Connection">The connection to the target it belongs to.</param>
/// <param name="PlcId">The configured target id.</param>
/// <param name="Options">That target's alarm options, carrying the acknowledge overrides.</param>
public sealed record AlarmAcknowledgeContext(
    PlcAlarm Alarm,
    IAdsConnection Connection,
    string PlcId,
    PlcAlarmTargetOptions Options);
