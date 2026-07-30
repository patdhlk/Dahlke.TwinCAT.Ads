using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// Validates <see cref="PlcAlarmsOptions"/> at application startup.
/// </summary>
/// <remarks>
/// Every failure is collected into one <see cref="ValidateOptionsResult"/> so an
/// operator sees the whole picture in a single boot failure rather than fixing
/// problems one restart at a time — the same contract as the core library's
/// <c>TwinCatAdsOptionsValidator</c>.
/// </remarks>
internal sealed class PlcAlarmsOptionsValidator(IOptions<TwinCatAdsOptions> adsOptions)
    : IValidateOptions<PlcAlarmsOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PlcAlarmsOptions options)
    {
        var failures = new List<string>();
        var configuredTargets = adsOptions.Value.Targets;

        foreach (var (plcId, target) in options.Targets)
        {
            if (!configuredTargets.ContainsKey(plcId))
            {
                failures.Add(
                    $"Alarm monitoring is configured for PLC target '{plcId}' " +
                    $"(PlcAlarms:Targets:{plcId}), but no such target exists under 'PlcTargets'. " +
                    $"Configured targets: {string.Join(", ", configuredTargets.Keys)}.");
            }

            if (string.IsNullOrWhiteSpace(target.SymbolPath))
            {
                failures.Add(
                    $"PlcAlarms:Targets:{plcId}:SymbolPath must name the PLC's alarm array " +
                    "(e.g. 'GVL.Errors').");
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
