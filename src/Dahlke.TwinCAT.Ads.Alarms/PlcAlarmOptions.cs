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
