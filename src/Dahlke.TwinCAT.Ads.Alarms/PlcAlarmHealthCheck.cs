using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// Reports health from the worst severity among the outstanding alarms.
/// </summary>
/// <remarks>
/// Internal, registered through a factory delegate — the public surface is the
/// registration extension only, matching the core library's health check.
/// </remarks>
internal sealed class PlcAlarmHealthCheck(
    IPlcAlarmMonitor monitor,
    AlarmSeverity degradedAt,
    AlarmSeverity unhealthyAt) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var outstanding = monitor.GetOutstanding();

        var data = outstanding
            .GroupBy(alarm => alarm.PlcId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (object)group
                    .Select(a => $"{a.Key} ({a.Severity})")
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        if (outstanding.Count == 0)
            return Task.FromResult(HealthCheckResult.Healthy("No outstanding alarms.", data));

        var worst = outstanding.Max(alarm => alarm.Severity);
        var description = $"{outstanding.Count} outstanding alarm(s); worst severity {worst}.";

        // Healthy is now the verdict that has to be EARNED; Degraded is the fallthrough. As a
        // ladder ending in `else Healthy` this failed OPEN below None: E_ErrorType is signed and
        // PlcAlarmBinder deliberately casts an unrecognised value through rather than dropping
        // the alarm, so (AlarmSeverity)(-1) reaches here, sorts BELOW None, clears neither
        // threshold, and was reported Healthy with "worst severity -1" in its own description —
        // a live alarm nothing can interpret, announced as fine.
        //
        // Stated as one condition on the Healthy arm rather than as an extra rung, so the answer
        // cannot depend on which threshold is compared first: degradedAt and unhealthyAt are
        // caller-supplied and their ordering is documented but unvalidated. Passing them the
        // wrong way round still leaves the Degraded band empty — AddTwinCatAdsAlarmHealthCheck
        // says so, and declines to guess which of the two a caller meant — but it can never
        // produce a Healthy for a severity that cleared a threshold the caller did set.
        var isHealthy = Enum.IsDefined(worst) && worst < degradedAt && worst < unhealthyAt;

        // Degraded, not Unhealthy, for an unrecognised severity: an outstanding alarm this
        // library cannot rank is a reason to look, not proof of a fault. Reporting Unhealthy
        // would claim more than the data supports and could pull an otherwise-serving instance
        // out of rotation over a PLC enum this package has not been taught yet.
        var result = worst >= unhealthyAt
            ? HealthCheckResult.Unhealthy(description, data: data)
            : isHealthy
                ? HealthCheckResult.Healthy(description, data)
                : HealthCheckResult.Degraded(description, data: data);

        return Task.FromResult(result);
    }
}
