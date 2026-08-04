using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dahlke.EtherCAT.Esi;

/// <summary>Registration for the ESI catalog.</summary>
public static class EsiServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEsiCatalog"/> as a singleton and binds <see cref="EsiOptions"/> from
    /// <paramref name="section"/>.
    /// </summary>
    /// <remarks>
    /// The singleton lifetime is load-bearing, not incidental: the catalog's guarantees — a device
    /// parsed at most once per process, and logged at most once per device — are properties of one
    /// shared instance and collapse silently under any other lifetime.
    ///
    /// <para>
    /// This does NOT eagerly resolve the catalog. Whether a misconfigured ESI directory should be
    /// reported at startup or on first use is a hosting decision belonging to the consumer, which
    /// can force it with <c>app.Services.GetRequiredService&lt;IEsiCatalog&gt;()</c> after building.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddEsiCatalog(this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        services.Configure<EsiOptions>(section);
        services.AddSingleton<IEsiCatalog, EsiCatalog>();

        return services;
    }
}
