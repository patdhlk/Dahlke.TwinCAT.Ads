namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// Severity of a PLC alarm, mirroring the PLC's <c>E_ErrorType</c> enumeration.
/// </summary>
/// <remarks>
/// The numeric values match <c>E_ErrorType</c> so the PLC value maps across
/// directly. A value outside this set is preserved rather than dropped — see
/// <see cref="PlcAlarm.Severity"/>.
/// </remarks>
public enum AlarmSeverity
{
    /// <summary>No severity reported.</summary>
    None = 0,

    /// <summary>Informational; no operator action implied.</summary>
    Info = 1,

    /// <summary>A condition worth attention that does not stop production.</summary>
    Warning = 2,

    /// <summary>A fault condition.</summary>
    Error = 3,
}
