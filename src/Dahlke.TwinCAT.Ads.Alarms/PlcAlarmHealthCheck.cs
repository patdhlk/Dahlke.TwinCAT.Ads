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

        var result = worst >= unhealthyAt
            ? HealthCheckResult.Unhealthy(description, data: data)
            : worst >= degradedAt
                ? HealthCheckResult.Degraded(description, data: data)
                : HealthCheckResult.Healthy(description, data);

        return Task.FromResult(result);
    }
}
