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
/// <para>
/// <b>Logged once per outage, not once per notification.</b> The mismatch is a property of
/// the PLC's type, so it recurs at cycle rate until someone changes the PLC — at the default
/// <c>CycleTimeMs</c> that would be five identical stack traces a second per target, for as
/// long as the fault lasts. The monitor reports the first and then counts the rest silently.
/// When a notification binds again it says so at <c>Information</c>, naming how many failed
/// in between, so the extent of the outage is still on the record; the next failure after
/// that recovery is reported in full again.
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
