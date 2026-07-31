using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// Validates the vendor-neutral parts of <see cref="PlcAlarmsOptions"/> at application startup.
/// </summary>
/// <remarks>
/// <para>
/// Every failure is collected into one <see cref="ValidateOptionsResult"/> so an
/// operator sees the whole picture in a single boot failure rather than fixing
/// problems one restart at a time — the same contract as the core library's
/// <c>TwinCatAdsOptionsValidator</c>.
/// </para>
/// <para>
/// Only rules that hold for every <see cref="IPlcAlarmDialect"/> belong here. Rules about how a
/// particular PLC acknowledges live with the dialect that reads them — see
/// <see cref="ErrorHandlerAlarmDialectOptionsValidator"/> — because this type cannot see which
/// dialect the container will resolve, and applying one vendor's rules to another's is how a
/// custom dialect ends up failed at boot over configuration it never reads.
/// </para>
/// </remarks>
internal sealed class PlcAlarmsOptionsValidator(IOptions<TwinCatAdsOptions> adsOptions)
    : IValidateOptions<PlcAlarmsOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PlcAlarmsOptions options)
    {
        var failures = new List<string>();

        // Targets is null only for a code-first caller that assigns it explicitly
        // (normal JSON binding always produces at least an empty dictionary). Null
        // means "no alarm targets configured", which is exactly as legal as an
        // empty dictionary — the package is opt-in per target — so there is
        // nothing to validate.
        if (options.Targets is null)
            return ValidateOptionsResult.Success;

        Dictionary<string, PlcTargetOptions>? configuredTargets;
        try
        {
            configuredTargets = adsOptions.Value.Targets;
        }
        catch (OptionsValidationException)
        {
            // IOptions<TwinCatAdsOptions>.Value re-runs TwinCatAdsOptionsValidator and
            // throws when the core options are themselves invalid; a failed Create()
            // is not cached, so it throws on every access. That core failure is
            // already reported by the core's own validator — swallowing it here is
            // deliberate, not an oversight. Re-throwing (or adding our own failure
            // about it) would either erase every alarm-specific failure below or
            // duplicate the core's message. Instead, fall back to "unknown" so the
            // cross-reference check below is skipped (we genuinely cannot tell
            // whether a plcId exists), while SymbolPath/CycleTimeMs checks — which
            // don't depend on the core options — still run and still get reported.
            configuredTargets = null;
        }

        foreach (var (plcId, target) in options.Targets)
        {
            if (configuredTargets is not null && !configuredTargets.ContainsKey(plcId))
            {
                failures.Add(
                    $"Alarm monitoring is configured for PLC target '{plcId}' " +
                    $"(PlcAlarms:Targets:{plcId}), but no such target exists under 'PlcTargets'. " +
                    $"Configured targets: {string.Join(", ", configuredTargets.Keys)}.");
            }

            if (string.IsNullOrWhiteSpace(target.SymbolPath))
            {
                // The exemplar keeps its parent segment because the built-in dialect derives the
                // acknowledging function block by trimming this path's last segment — 'GVL.Errors'
                // would suggest a layout deriving 'GVL', which owns no such block. That rule now
                // lives in ErrorHandlerAlarmDialectOptionsValidator; the example stays consistent
                // with it, since this message is what an operator sees first.
                failures.Add(
                    $"PlcAlarms:Targets:{plcId}:SymbolPath must name the PLC's alarm array " +
                    "(e.g. 'MAIN.ErrorHandler.aHmiAlarms').");
            }

            if (target.CycleTimeMs <= 0)
            {
                failures.Add(
                    $"PlcAlarms:Targets:{plcId}:CycleTimeMs must be greater than zero " +
                    $"(was {target.CycleTimeMs}).");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
