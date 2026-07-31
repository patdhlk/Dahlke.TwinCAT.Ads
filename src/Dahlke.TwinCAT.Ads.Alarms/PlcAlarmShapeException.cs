namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// Thrown when a PLC alarm array does not have the shape this package binds.
/// </summary>
/// <remarks>
/// <para>
/// <b>The binder refuses to guess.</b> A renamed or retyped PLC member has no correct
/// reading, and degrading to a default would produce a plausible-looking but wrong alarm
/// list indefinitely — which for alarms is worse than no list at all. So this is thrown
/// rather than defaulted, with a message naming the member and the symbol path so the fix
/// is mechanical.
/// </para>
/// <para>
/// <b>Loud, but not fatal to monitoring.</b> The monitor catches this, logs it at
/// <c>Error</c> naming the member and the symbol path, and drops that whole snapshot
/// rather than publishing a half-bound one — the outstanding set keeps its last good
/// reading instead of emptying. The subscription stays live, so monitoring resumes on the
/// next notification that binds and a transient malformation recovers by itself. There is
/// nothing for a consumer to restart or re-register in response; a repeat means the PLC's
/// <c>ST_ErrorEntry</c> really has changed and someone has to fix it.
/// </para>
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
