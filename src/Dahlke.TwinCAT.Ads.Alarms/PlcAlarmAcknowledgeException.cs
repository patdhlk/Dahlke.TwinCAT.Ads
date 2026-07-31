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
    public PlcAlarmAcknowledgeException(string message, string? returnCodeName, long? returnCode)
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

    /// <summary>
    /// The raw value the method returned, as it came off the wire, or <see langword="null"/>
    /// when no <see cref="long"/> can carry it — the PLC returned something non-integral, or
    /// an integral value beyond <see cref="long.MaxValue"/>.
    /// </summary>
    /// <remarks>
    /// Nullable so that "the PLC said something this package cannot read as a number" is
    /// distinguishable from "the PLC returned <c>0</c> and <c>0</c> names no member it
    /// publishes". Reported as a fabricated <c>0</c>, those two are the same pair of property
    /// values, and a caller branching on the properties rather than parsing
    /// <see cref="Exception.Message"/> cannot tell them apart.
    /// </remarks>
    public long? ReturnCode { get; }
}
