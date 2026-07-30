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
    /// The fully-qualified symbol path of the PLC's alarm array, e.g. <c>GVL.Errors</c>.
    /// Required.
    /// </summary>
    public string SymbolPath { get; set; } = string.Empty;

    /// <summary>
    /// How often the PLC pushes array changes, in milliseconds. Must be positive.
    /// </summary>
    public int CycleTimeMs { get; set; } = 200;

    /// <summary>What clock the PLC's <c>TIMESTRUCT</c> is expressed in.</summary>
    public PlcClockKind PlcClock { get; set; } = PlcClockKind.Unspecified;
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
    /// One catalog serves the fleet: <c>sKey</c> is <c>'&lt;BMK&gt;Err&lt;Code&gt;'</c>
    /// and BMKs identify equipment across the plant, so the key is already globally
    /// meaningful. A deployment needing per-target text registers its own
    /// <see cref="IAlarmTextCatalog"/>.
    /// </remarks>
    public string? TextCatalog { get; set; }

    /// <summary>
    /// Per-target alarm settings, keyed by the same target id as <c>PlcTargets</c>.
    /// Targets absent from this dictionary are not monitored.
    /// </summary>
    public Dictionary<string, PlcAlarmTargetOptions> Targets { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
