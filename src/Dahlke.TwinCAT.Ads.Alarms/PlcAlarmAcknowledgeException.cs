namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>Thrown when a PLC refuses an acknowledgement for a reason other than "no such alarm".</summary>
/// <remarks>
/// A refusal is not the same as a vanished alarm, and collapsing the two into
/// <see langword="false"/> would tell a caller to stop asking when the right answer is often to
/// try again. <see cref="ReturnCodeName"/> carries the PLC's own name for the outcome so a
/// caller can branch on it without parsing this message.
/// </remarks>
public sealed class PlcAlarmAcknowledgeException : InvalidOperationException
{
    /// <summary>Creates the exception for a named PLC return code.</summary>
    public PlcAlarmAcknowledgeException(string message, string? returnCodeName, long returnCode)
        : base(message)
    {
        ReturnCodeName = returnCodeName;
        ReturnCode = returnCode;
    }

    /// <summary>
    /// The PLC's name for the outcome, e.g. <c>BUSY</c>, or <see langword="null"/> when the
    /// returned value matched no member the PLC publishes.
    /// </summary>
    public string? ReturnCodeName { get; }

    /// <summary>The raw numeric value the method returned, as it came off the wire.</summary>
    public long ReturnCode { get; }
}
