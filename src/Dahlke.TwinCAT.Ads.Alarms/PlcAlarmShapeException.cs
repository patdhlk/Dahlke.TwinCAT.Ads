namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// Thrown when a PLC alarm array does not have the shape this package binds.
/// </summary>
/// <remarks>
/// This is deliberately fatal to the monitor rather than survivable. A renamed or
/// retyped PLC member has no correct reading, and degrading to a default would
/// produce a plausible-looking but wrong alarm list indefinitely — which for alarms
/// is worse than no list at all. The message names the member and the symbol path so
/// the fix is mechanical.
/// </remarks>
public sealed class PlcAlarmShapeException : InvalidOperationException
{
    /// <summary>Creates the exception with a message naming the member and symbol path.</summary>
    public PlcAlarmShapeException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    public PlcAlarmShapeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
