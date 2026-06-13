using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// ASP.NET Core health check for the TwinCAT ADS connection pool.
/// </summary>
/// <remarks>
/// <para>
/// Reports one of three statuses based on the state of all configured PLC targets:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>Healthy</b> — every configured target is
///     <see cref="ConnectionState.Connected"/>.
///   </item>
///   <item>
///     <b>Degraded</b> — at least one target is connected and at least one is not.
///   </item>
///   <item>
///     <b>Unhealthy</b> — no target is connected (covers the router-never-became-
///     ready scenario, in which real-target loops have not yet been released, as
///     well as a total outage of all targets).
///   </item>
///   <item>
///     <b>Failure status</b> — no PLC targets are configured (returns the
///     <c>failureStatus</c> from the registration, defaulting to Unhealthy).
///   </item>
/// </list>
/// <para>
/// The health-check response data dictionary (available in the JSON output of
/// <c>/health</c> when the full HealthChecks middleware is used) includes one entry
/// per configured target whose value is a string representation of its current
/// <see cref="ConnectionState"/>. This gives dashboards and operators per-target
/// visibility without additional endpoints.
/// </para>
/// <para>
/// Register via the extension method:
/// <code>
/// builder.Services
///     .AddHealthChecks()
///     .AddTwinCatAdsHealthCheck();
/// </code>
/// </para>
/// </remarks>
internal sealed class TwinCatAdsHealthCheck : IHealthCheck
{
    private readonly AdsConnectionPool _pool;

    internal TwinCatAdsHealthCheck(AdsConnectionPool pool)
    {
        _pool = pool;
    }

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var targets = _pool.GetTargetStates();

        // Build the per-target data dictionary for dashboards and logging.
        var data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (plcId, _, state) in targets)
            data[plcId] = state.ToString();

        if (targets.Count == 0)
        {
            return Task.FromResult(new HealthCheckResult(
                context.Registration.FailureStatus,
                "No PLC targets are configured.",
                data: data));
        }

        var connectedCount = targets.Count(t => t.State == ConnectionState.Connected);
        var realTargets = targets.Where(t => t.Mode == ConnectionMode.Real).ToList();
        var connectedRealCount = realTargets.Count(t => t.State == ConnectionState.Connected);

        // All targets connected — healthy.
        if (connectedCount == targets.Count)
        {
            return Task.FromResult(new HealthCheckResult(
                HealthStatus.Healthy,
                $"All {targets.Count} target(s) connected.",
                data: data));
        }

        // At least one target is connected — degraded.
        if (connectedCount > 0)
        {
            var disconnected = targets
                .Where(t => t.State != ConnectionState.Connected)
                .Select(t => t.PlcId);
            var description = $"Degraded: {string.Join(", ", disconnected)} not connected.";
            return Task.FromResult(new HealthCheckResult(
                HealthStatus.Degraded,
                description,
                data: data));
        }

        // No target connected at all.
        // If real targets exist and none are connected, check whether router loops
        // have been released. All-real-disconnected with unreleased loops means the
        // router has not yet become ready (or never will).
        if (realTargets.Count > 0 && connectedRealCount == 0)
        {
            var description = "Unhealthy: all real target(s) disconnected. Router may not be ready yet.";
            return Task.FromResult(new HealthCheckResult(
                context.Registration.FailureStatus,
                description,
                data: data));
        }

        // All-sim config with none connected yet (starting up) — unhealthy until
        // simulated targets complete their (near-instant) connection loop.
        return Task.FromResult(new HealthCheckResult(
            context.Registration.FailureStatus,
            "Unhealthy: no targets connected.",
            data: data));
    }
}
