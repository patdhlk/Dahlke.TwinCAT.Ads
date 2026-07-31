using System.Globalization;

namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// The shipped default dialect: binds <c>ST_ErrorEntry</c> and acknowledges through
/// <c>FB_ErrorHandler.AcknowledgeAlarm</c>.
/// </summary>
/// <remarks>
/// Verified against a live PLC on 2026-07-31, where the method is declared
/// <c>deaReturnType AcknowledgeAlarm([in] STRING(80) sKeyToAck)</c> and requires
/// <c>{attribute 'TcRpcEnable'}</c> to be reachable over ADS.
/// </remarks>
internal sealed class ErrorHandlerAlarmDialect : IPlcAlarmDialect
{
    private const string ResultTypeName = "deaReturnType";

    private const string Success = "SUCCESS";
    private const string NotFound = "NOT_FOUND";

    /// <inheritdoc />
    public IReadOnlyList<PlcAlarm> Bind(AlarmBindContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return PlcAlarmBinder.Bind(
            context.NotificationValue, context.PlcId, context.SymbolPath,
            context.PlcClock, context.Logger);
    }

    /// <inheritdoc />
    public async Task<bool> AcknowledgeAsync(AlarmAcknowledgeContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var instancePath = ResolveInstancePath(context);

        // Resolved BEFORE the acknowledgement is issued, and never after it.
        //
        // This call fails in several ordinary ways — the type not published, the type not an
        // enum, the caller cancelling, and the per-target timeout elapsing (which the core
        // enforces around this call by resolving off the calling thread; before 0.7.0 it could
        // not, and a cold type-system upload was unbounded). Every one of them, downstream of
        // the RPC, would surface a failure for an alarm the PLC HAD already acknowledged: the
        // operator presses the button again on something that already worked. The core caches an
        // enum's members per connection for the connection's life, so hoisting it costs nothing
        // and leaves only the unavoidable value-interpretation step after the call.
        var members = await context.Connection
            .GetEnumMembersAsync(ResultTypeName, ct).ConfigureAwait(false);

        var result = await context.Connection
            .InvokeRpcMethodAsync(instancePath, context.Options.AcknowledgeMethod, [context.Alarm.Key], ct)
            .ConfigureAwait(false);

        var raw = ToReturnCode(result.ReturnValue, instancePath, context);

        // By NAME — never by number, and never by position. Numbering moves while names do not
        // (the reference rack publishes a numbering its own source no longer agrees with), and
        // GetEnumMembersAsync promises DECLARATION ORDER, not dense zero-based values:
        // `SUCCESS := 100` is ordinary ST, so members[raw] would read the wrong member entirely.
        var name = members.FirstOrDefault(m => m.Value == raw)?.Name;

        if (string.Equals(name, Success, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(name, NotFound, StringComparison.OrdinalIgnoreCase))
            return false;

        throw new PlcAlarmAcknowledgeException(
            name is null
                ? $"Acknowledging '{context.Alarm.Key}' on PLC '{context.PlcId}' returned {raw}, " +
                  $"which matches no member of '{ResultTypeName}' the PLC publishes."
                : $"PLC '{context.PlcId}' refused to acknowledge '{context.Alarm.Key}': {name}.",
            name, raw);
    }

    /// <summary>Works out which function block owns acknowledgement for this target.</summary>
    /// <exception cref="InvalidOperationException">
    /// The path cannot be derived and none was configured.
    /// </exception>
    /// <remarks>
    /// Deliberately NOT <see cref="PlcAlarmAcknowledgeException"/>: that type means the PLC
    /// refused, and nothing was ever sent for it to refuse. Its
    /// <see cref="PlcAlarmAcknowledgeException.ReturnCode"/> could carry <see langword="null"/>
    /// honestly enough, but the type itself would still report a configuration error as a PLC
    /// outcome — and a caller that retries on refusal would be retrying something only an edit
    /// to configuration can fix. This never reached the PLC, and it says so by its type.
    /// </remarks>
    private static string ResolveInstancePath(AlarmAcknowledgeContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.Options.AcknowledgeInstancePath))
            return context.Options.AcknowledgeInstancePath!;

        var path = context.Options.SymbolPath;
        var lastDot = path.LastIndexOf('.');

        if (lastDot <= 0)
            throw new InvalidOperationException(
                $"Cannot derive the acknowledging function block from SymbolPath '{path}' on PLC " +
                $"'{context.PlcId}'. Set PlcAlarms:Targets:{context.PlcId}:AcknowledgeInstancePath " +
                "explicitly.");

        return path[..lastDot];
    }

    /// <summary>Narrows the method's return value to the integral code the enum is keyed by.</summary>
    /// <remarks>
    /// Integral types only — deliberately not <see cref="IConvertible"/>, which is not a test for
    /// "is numeric". A <see cref="string"/> satisfies it and then escapes as a raw
    /// <see cref="FormatException"/>, and a <see cref="bool"/> converts silently to <c>0</c>/<c>1</c>.
    /// Since <see cref="PlcAlarmTargetOptions.AcknowledgeMethod"/> is a public knob, a
    /// <c>BOOL</c>-returning method pointed at by it would then report whichever member happens to
    /// hold that number — under a zero-based <c>SUCCESS</c>, a failed acknowledgement read as
    /// success. Every PLC enum base from <c>SINT</c> to <c>ULINT</c> is accepted; nothing else is.
    /// </remarks>
    private static long ToReturnCode(
        object? value, string instancePath, AlarmAcknowledgeContext context) => value switch
    {
        sbyte v => v,
        byte v => v,
        short v => v,
        ushort v => v,
        int v => v,
        uint v => v,
        long v => v,
        ulong v when v <= long.MaxValue => (long)v,

        // Matches AdsEnumMember's own contract: a ULINT value past long.MaxValue throws rather
        // than wrapping into a negative that would match some other member. The value is in
        // the message but NOT in ReturnCode: there is no long that carries it, and null is
        // what that property means by "nothing numeric this package can hand you".
        ulong v => throw new PlcAlarmAcknowledgeException(
            $"'{instancePath}.{context.Options.AcknowledgeMethod}' on PLC '{context.PlcId}' returned " +
            $"{v}, which exceeds long.MaxValue; a {ResultTypeName} that large is not supported.",
            null, null),

        // null, not 0: a fabricated 0 here is indistinguishable from a genuine 0 that matched
        // no member, and 0 is SUCCESS under some numberings.
        _ => throw new PlcAlarmAcknowledgeException(
            $"'{instancePath}.{context.Options.AcknowledgeMethod}' on PLC '{context.PlcId}' returned " +
            $"{Describe(value)}, which is not an integral {ResultTypeName} value.",
            null, null),
    };

    private static string Describe(object? value) => value is null
        ? "null"
        : string.Create(CultureInfo.InvariantCulture, $"{value.GetType().Name} '{value}'");
}
