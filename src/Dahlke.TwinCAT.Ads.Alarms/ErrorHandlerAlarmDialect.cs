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

        var result = await context.Connection
            .InvokeRpcMethodAsync(instancePath, context.Options.AcknowledgeMethod, [context.Alarm.Key], ct)
            .ConfigureAwait(false);

        var raw = ToInt64(result.ReturnValue, instancePath, context);

        // Resolved by NAME, never by number: PLC enum numbering moves while names do not, and
        // the reference rack currently publishes a different numbering than its own source.
        var members = await context.Connection
            .GetEnumMembersAsync(ResultTypeName, ct).ConfigureAwait(false);

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

    private static string ResolveInstancePath(AlarmAcknowledgeContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.Options.AcknowledgeInstancePath))
            return context.Options.AcknowledgeInstancePath!;

        var path = context.Options.SymbolPath;
        var lastDot = path.LastIndexOf('.');

        if (lastDot <= 0)
            throw new PlcAlarmAcknowledgeException(
                $"Cannot derive the acknowledging function block from SymbolPath '{path}' on PLC " +
                $"'{context.PlcId}'. Set PlcAlarms:Targets:{context.PlcId}:AcknowledgeInstancePath " +
                "explicitly.", null, 0);

        return path[..lastDot];
    }

    private static long ToInt64(object? value, string instancePath, AlarmAcknowledgeContext context)
    {
        if (value is IConvertible convertible)
            return convertible.ToInt64(CultureInfo.InvariantCulture);

        throw new PlcAlarmAcknowledgeException(
            $"'{instancePath}.{context.Options.AcknowledgeMethod}' on PLC '{context.PlcId}' returned " +
            $"{value?.GetType().Name ?? "null"}, which is not a numeric {ResultTypeName} value.",
            null, 0);
    }
}
