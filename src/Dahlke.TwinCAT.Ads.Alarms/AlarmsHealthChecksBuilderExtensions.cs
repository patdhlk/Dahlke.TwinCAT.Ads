using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// Extension methods for adding the PLC alarm health check to an
/// <see cref="IHealthChecksBuilder"/>.
/// </summary>
public static class AlarmsHealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds a health check that reports from the worst severity among the outstanding
    /// alarms.
    /// </summary>
    /// <param name="builder">The builder to add the check to.</param>
    /// <param name="name">The registration name (default: <c>"twincat_ads_alarms"</c>).</param>
    /// <param name="degradedAt">
    /// The lowest severity that reports Degraded. Must be less than or equal to
    /// <paramref name="unhealthyAt"/> — see the remarks.
    /// </param>
    /// <param name="unhealthyAt">
    /// The lowest severity that reports Unhealthy. Must be greater than or equal to
    /// <paramref name="degradedAt"/> — see the remarks.
    /// </param>
    /// <param name="tags">Optional tags attached to the registration.</param>
    /// <remarks>
    /// <para>
    /// <c>AddTwinCatAdsAlarms</c> must be registered before this; the monitor is resolved
    /// at the first evaluation, so its absence surfaces then rather than at startup.
    /// </para>
    /// <para>
    /// <b><paramref name="degradedAt"/> must be less than or equal to
    /// <paramref name="unhealthyAt"/>.</b> The worst outstanding severity is tested against
    /// <paramref name="unhealthyAt"/> FIRST and against <paramref name="degradedAt"/> only
    /// if that fails, so passing the two the wrong way round makes Degraded unreachable:
    /// every severity from <paramref name="unhealthyAt"/> — now the lower of the two —
    /// upwards reports Unhealthy, everything below it reports Healthy, and nothing ever
    /// lands in the band between them. This is not validated: with the arguments swapped
    /// there is no reading of the caller's intent this method could honour instead. If a
    /// check reports Unhealthy where Degraded was expected, look here first.
    /// </para>
    /// <para>
    /// <b>What this check does and does not tell you.</b> It reports ONLY the severity
    /// of alarms currently outstanding, from <see cref="IPlcAlarmMonitor.GetOutstanding()"/>.
    /// It does NOT indicate whether alarm monitoring is live for a given target: a target
    /// that is still awaiting its first connection has no subscription yet, reports no
    /// alarms, and therefore appears healthy here — indistinguishable from a target that is
    /// connected and genuinely alarm-free. Register
    /// <see cref="global::Dahlke.TwinCAT.Ads.HealthChecksBuilderExtensions.AddTwinCatAdsHealthCheck"/>
    /// alongside this one for per-target connectivity; the two together are what give the
    /// full picture — this one says whether the alarms it can see are bad, that one says
    /// whether it can see a given target at all.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddTwinCatAds(builder.Configuration)
    ///     .AddTwinCatAdsAlarms(builder.Configuration)
    ///     .AddHealthChecks()
    ///     .AddTwinCatAdsAlarmHealthCheck();
    /// </code>
    /// </example>
    public static IHealthChecksBuilder AddTwinCatAdsAlarmHealthCheck(
        this IHealthChecksBuilder builder,
        string name = "twincat_ads_alarms",
        AlarmSeverity degradedAt = AlarmSeverity.Warning,
        AlarmSeverity unhealthyAt = AlarmSeverity.Error,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Add(new HealthCheckRegistration(
            name,
            sp => new PlcAlarmHealthCheck(
                sp.GetRequiredService<IPlcAlarmMonitor>(), degradedAt, unhealthyAt),
            failureStatus: null,
            tags));

        return builder;
    }
}
