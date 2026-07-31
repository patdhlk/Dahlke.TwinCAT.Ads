using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// Validates the <see cref="PlcAlarmsOptions"/> members that the built-in
/// <see cref="ErrorHandlerAlarmDialect"/> reads.
/// </summary>
/// <remarks>
/// <para>
/// Registered by <c>AddTwinCatAdsAlarms</c> only when it also registers that dialect. A
/// consumer who registered their own <see cref="IPlcAlarmDialect"/> first gets neither, and
/// brings their own <see cref="IValidateOptions{TOptions}"/> or none. That pairing is the whole
/// point: both rules below are <c>FB_ErrorHandler</c> vocabulary, and applying them to a dialect
/// that acknowledges by a pulsed trigger, a different RPC shape, or a write to a request array
/// fails a boot over configuration nothing will ever read.
/// </para>
/// <para>
/// Failures are collected rather than returned one at a time, matching
/// <see cref="PlcAlarmsOptionsValidator"/>. The options infrastructure runs every registered
/// validator and concatenates their failures, so an operator still sees one complete picture per
/// boot even though the rules now live in two types.
/// </para>
/// </remarks>
internal sealed class ErrorHandlerAlarmDialectOptionsValidator : IValidateOptions<PlcAlarmsOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PlcAlarmsOptions options)
    {
        var failures = new List<string>();

        // Null Targets means "no alarm targets configured" — as legal here as it is for the
        // vendor-neutral validator, because the package is opt-in per target.
        if (options.Targets is null)
            return ValidateOptionsResult.Success;

        foreach (var (plcId, target) in options.Targets)
        {
            if (string.IsNullOrWhiteSpace(target.AcknowledgeMethod))
            {
                failures.Add(
                    $"PlcAlarms:Targets:{plcId}:AcknowledgeMethod must name the PLC method that " +
                    "acknowledges one alarm by key (default 'AcknowledgeAlarm').");
            }

            if (string.IsNullOrWhiteSpace(target.AcknowledgeInstancePath)
                && target.SymbolPath?.LastIndexOf('.') is null or <= 0)
            {
                // The ordering fix belongs in the message, not only in the XML docs: an operator
                // meets this as a boot failure, and "register your dialect earlier" is not a
                // guess anyone makes unprompted. It replaces 0.7.0's advice to satisfy the rule
                // with any non-blank value — that workaround is no longer the answer.
                failures.Add(
                    $"PlcAlarms:Targets:{plcId}:AcknowledgeInstancePath must be set, because the " +
                    $"acknowledging function block cannot be derived from SymbolPath " +
                    $"'{target.SymbolPath}' — it has no parent segment to trim. This rule belongs " +
                    "to the built-in FB_ErrorHandler dialect. If you registered your own " +
                    "IPlcAlarmDialect, register it BEFORE calling AddTwinCatAdsAlarms so its own " +
                    "validation replaces this rule.");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
