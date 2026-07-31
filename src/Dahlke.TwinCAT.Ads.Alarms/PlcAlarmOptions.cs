namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// Declares what clock the PLC's <c>TIMESTRUCT</c> is expressed in.
/// </summary>
/// <remarks>
/// <c>TIMESTRUCT</c> carries no time zone, so this cannot be inferred. The default
/// states no claim rather than guessing — stamping a wrong
/// <see cref="DateTimeKind"/> silently shifts every alarm timestamp for consumers
/// that convert.
/// </remarks>
public enum PlcClockKind
{
    /// <summary>No claim; timestamps are <see cref="DateTimeKind.Unspecified"/>.</summary>
    Unspecified = 0,

    /// <summary>The PLC clock runs in UTC.</summary>
    Utc = 1,

    /// <summary>The PLC clock runs in the host's local time.</summary>
    Local = 2,
}

/// <summary>Alarm monitoring settings for one configured PLC target.</summary>
public sealed class PlcAlarmTargetOptions
{
    /// <summary>
    /// The fully-qualified symbol path of the PLC's alarm array, e.g.
    /// <c>MAIN.ErrorHandler.aHmiAlarms</c>. Required.
    /// </summary>
    /// <remarks>
    /// The exemplar has a parent segment on purpose: the built-in dialect derives
    /// <see cref="AcknowledgeInstancePath"/> by trimming this path's last segment, so
    /// <c>GVL.Errors</c> would derive <c>GVL</c> — which owns no acknowledging function block,
    /// and which nothing catches until an acknowledgement is attempted against it. A custom
    /// <see cref="IPlcAlarmDialect"/> derives nothing from this path unless it chooses to.
    /// </remarks>
    public string SymbolPath { get; set; } = string.Empty;

    /// <summary>
    /// How often the PLC pushes array changes, in milliseconds. Must be positive.
    /// </summary>
    public int CycleTimeMs { get; set; } = 200;

    /// <summary>What clock the PLC's <c>TIMESTRUCT</c> is expressed in.</summary>
    public PlcClockKind PlcClock { get; set; } = PlcClockKind.Unspecified;

    /// <summary>
    /// The instance path of the function block that owns acknowledgement, e.g.
    /// <c>MAIN.ErrorHandler</c>. When absent it is derived by trimming the last segment of
    /// <see cref="SymbolPath"/>, which is right when the alarm array is a member of that block
    /// and wrong otherwise — set it explicitly for any other layout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This configures the built-in <c>FB_ErrorHandler</c> dialect.</b> A custom
    /// <see cref="IPlcAlarmDialect"/> receives it on <see cref="AlarmAcknowledgeContext"/> and
    /// is free to ignore it entirely.
    /// </para>
    /// <para>
    /// <b>The startup rule that requires it belongs to that dialect too.</b> Validation fails
    /// when this is blank AND <see cref="SymbolPath"/> has no parent segment to trim, but only
    /// when the built-in dialect is the one registered. Register your own
    /// <see cref="IPlcAlarmDialect"/> before <c>AddTwinCatAdsAlarms</c> and neither the rule nor
    /// this member applies to you — bring your own <c>IValidateOptions&lt;PlcAlarmsOptions&gt;</c>
    /// for whatever your dialect does need.
    /// </para>
    /// <para>
    /// If ordering genuinely cannot be controlled — a wrapper library calls
    /// <c>AddTwinCatAdsAlarms</c> internally, say — setting this to any non-blank value still
    /// satisfies the rule and is passed through unread, exactly as in 0.7.0. That is a fallback
    /// for that situation, not a recommendation: registering your dialect first remains the
    /// right answer whenever you control the ordering.
    /// </para>
    /// </remarks>
    public string? AcknowledgeInstancePath { get; set; }

    /// <summary>The PLC method that acknowledges one alarm by key.</summary>
    /// <remarks>
    /// Configures the built-in <c>FB_ErrorHandler</c> dialect, whose method is
    /// <c>AcknowledgeAlarm</c> — hence the default. A custom <see cref="IPlcAlarmDialect"/>
    /// receives it on <see cref="AlarmAcknowledgeContext"/> and may ignore it; the rule that it
    /// be non-blank is the built-in dialect's own, and is not applied when another dialect is
    /// registered before <c>AddTwinCatAdsAlarms</c>.
    /// </remarks>
    public string AcknowledgeMethod { get; set; } = "AcknowledgeAlarm";
}

/// <summary>
/// Root options for alarm monitoring, bound from the <c>PlcAlarms</c> configuration
/// section.
/// </summary>
public sealed class PlcAlarmsOptions
{
    /// <summary>
    /// Path to a JSON alarm-text catalog mapping <c>sKey</c> to human-readable text, or
    /// <see langword="null"/> for no catalog.
    /// </summary>
    /// <remarks>
    /// One catalog serves the fleet: <c>sKey</c> is the PLC's own composite key combining
    /// the equipment identifier and the error code, and BMKs identify equipment across the
    /// plant, so the key is already globally meaningful — its exact spelling is the PLC
    /// program's business and this package never parses it. A deployment needing per-target
    /// text registers its own <see cref="IAlarmTextCatalog"/>.
    /// </remarks>
    public string? TextCatalog { get; set; }

    /// <summary>
    /// Per-target alarm settings, keyed by the same target id as <c>PlcTargets</c>.
    /// Targets absent from this dictionary are not monitored.
    /// </summary>
    public Dictionary<string, PlcAlarmTargetOptions> Targets { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
