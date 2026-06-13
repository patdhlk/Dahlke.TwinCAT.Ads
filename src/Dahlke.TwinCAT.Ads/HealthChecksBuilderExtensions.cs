using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Extension methods for adding the TwinCAT ADS health check to an
/// <see cref="IHealthChecksBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// The health check is implemented as an internal type and registered via a factory
/// delegate — it does not need to be a public, DI-discoverable type. The public
/// surface is the registration extension only.
/// </para>
/// </remarks>
public static class HealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds a TwinCAT ADS health check to the <see cref="IHealthChecksBuilder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHealthChecksBuilder"/> to add the check to.</param>
    /// <param name="name">
    /// The registration name surfaced in health-check responses (default:
    /// <c>"twincat_ads"</c>).
    /// </param>
    /// <param name="failureStatus">
    /// The <see cref="HealthStatus"/> to report when the health check fails (i.e.
    /// no targets are connected). Defaults to <see cref="HealthStatus.Unhealthy"/>
    /// when <see langword="null"/>.
    /// </param>
    /// <param name="tags">
    /// Optional tags attached to the registration, e.g. <c>["plc", "automation"]</c>.
    /// </param>
    /// <returns>
    /// The same <paramref name="builder"/> for chaining, so that multiple health
    /// checks can be registered fluently.
    /// </returns>
    /// <example>
    /// <code>
    /// // In Program.cs or Startup.cs:
    /// builder.Services
    ///     .AddTwinCatAds(builder.Configuration)
    ///     .AddHealthChecks()
    ///     .AddTwinCatAdsHealthCheck();
    ///
    /// // Then map the endpoint in the app pipeline:
    /// app.MapHealthChecks("/health");
    /// </code>
    /// </example>
    /// <remarks>
    /// <para>
    /// <b>Operator note:</b> <c>AddTwinCatAds</c>
    /// (or <c>AddTwinCatAdsSimulation</c>) must be registered in DI before calling
    /// this method. The connection pool is resolved lazily via
    /// <c>sp.GetRequiredService&lt;AdsConnectionPool&gt;()</c> at the first health-check
    /// evaluation, not at registration time. If the pool is absent the failure surfaces
    /// late — as a runtime <see cref="System.InvalidOperationException"/> thrown from DI
    /// on the first health-check invocation, rather than at application startup.
    /// </para>
    /// </remarks>
    public static IHealthChecksBuilder AddTwinCatAdsHealthCheck(
        this IHealthChecksBuilder builder,
        string name = "twincat_ads",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        builder.Add(new HealthCheckRegistration(
            name,
            sp => new TwinCatAdsHealthCheck(sp.GetRequiredService<AdsConnectionPool>()),
            failureStatus,
            tags));

        return builder;
    }
}
