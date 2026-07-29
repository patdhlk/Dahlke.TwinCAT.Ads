using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Guards the FAILURE CLASS behind the dead <c>RawChannels</c> section: an options
/// sub-object added to <see cref="TwinCatAdsOptions"/> and never wired into the
/// configuration binder.
/// </summary>
/// <remarks>
/// <para>
/// <c>RawChannels</c> shipped through nine reviewed tasks and 807 passing tests
/// reading nothing from configuration, because <c>BindTwinCatAdsOptions</c> binds
/// each section EXPLICITLY — <c>PlcTargets</c>, <c>AmsRouter:NetId</c>,
/// <c>AdsSymbolDump</c> — and nothing checks that the list is complete. Fixing the
/// instance does not fix the class; this does.
/// </para>
/// <para>
/// <b>How it is made to fail.</b> <see cref="EverySettableOptionIsDeclaredBindable"/>
/// reflects over <see cref="TwinCatAdsOptions"/> and compares what it finds against
/// <see cref="Registry"/>. Adding a property without adding a registry entry fails
/// it. The registry is not a list to be rubber-stamped, because
/// <see cref="EveryDeclaredOptionIsReachableFromConfiguration"/> then BINDS each
/// declared path through the real registration path and asserts the value arrives —
/// so satisfying the first test by inventing a plausible-looking entry fails the
/// second. Both are needed: names alone cannot be derived from the binder, since
/// every section has an idiosyncratic layout (a legacy key, a nested re-bind, a
/// single scalar).
/// </para>
/// <para>
/// <b>Its limit, stated rather than implied.</b> This covers the TOP LEVEL of
/// <see cref="TwinCatAdsOptions"/>, which is where a section lives. A new member
/// added to an ALREADY-bound sub-object is not covered — and does not need to be
/// where that sub-object is bound with a whole-section <c>Bind</c>, as
/// <c>RawChannels</c> now is, because <c>Bind</c> picks up new members for free. It
/// WOULD be missed for a sub-object bound property-by-property, which
/// <c>AmsRouterOptions</c> is: only <c>NetId</c> is read. That gap is real and is
/// the next one worth closing.
/// </para>
/// </remarks>
public class OptionsSectionsAreBoundTests
{
    /// <summary>
    /// One public member of <see cref="TwinCatAdsOptions"/>, the configuration key
    /// that reaches it, and a probe value distinct from its default.
    /// </summary>
    /// <param name="Property">The property name on <see cref="TwinCatAdsOptions"/>.</param>
    /// <param name="Key">A configuration key that must land on it.</param>
    /// <param name="Value">The value to write at <paramref name="Key"/>.</param>
    /// <param name="Read">Reads back what <paramref name="Key"/> should have set.</param>
    /// <param name="Expected">
    /// What <paramref name="Read"/> must return. Deliberately never a default, so a
    /// binder that ignores the key cannot pass by accident.
    /// </param>
    private sealed record BindableOption(
        string Property,
        string Key,
        string Value,
        Func<TwinCatAdsOptions, object?> Read,
        object? Expected);

    private static readonly BindableOption[] Registry =
    [
        new("Targets",
            "PlcTargets:probe:AmsNetId", "7.7.7.7.7.7",
            o => o.Targets["probe"].AmsNetId, "7.7.7.7.7.7"),

        new("Router",
            "AmsRouter:NetId", "9.9.9.9.9.9",
            o => o.Router.NetId, "9.9.9.9.9.9"),

        new("Diagnostics",
            "AdsSymbolDump:MaxDepth", "7",
            o => o.Diagnostics.SymbolDump.MaxDepth, 7),

        new("RawChannels",
            "RawChannels:TimeoutMs", "4321",
            o => o.RawChannels.TimeoutMs, 4321),

    ];

    /// <summary>
    /// Public settable instance properties of <see cref="TwinCatAdsOptions"/> — the
    /// configuration surface a host can populate.
    /// </summary>
    private static IEnumerable<string> DiscoverOptionProperties() =>
        typeof(TwinCatAdsOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.SetMethod is { IsPublic: true })
            .Select(p => p.Name);

    [Fact]
    public void EverySettableOptionIsDeclaredBindable()
    {
        var discovered = DiscoverOptionProperties().OrderBy(n => n).ToArray();
        var declared = Registry.Select(e => e.Property).OrderBy(n => n).ToArray();

        Assert.Equal(declared, discovered);
    }

    /// <summary>
    /// Each declared key really reaches its property, through the registration path a
    /// host uses. This is what stops the registry from being a rubber stamp.
    /// </summary>
    [Theory]
    [MemberData(nameof(RegistryKeys))]
    public void EveryDeclaredOptionIsReachableFromConfiguration(string property)
    {
        var entry = Registry.Single(e => e.Property == property);

        var settings = new Dictionary<string, string?>
        {
            // Startup validation rejects an empty Targets collection, so every case
            // needs a target. Harmless for the Targets case itself: that entry writes
            // its own key over this one.
            ["PlcTargets:scaffold:AmsNetId"] = "1.2.3.4.5.6",
            [entry.Key] = entry.Value,
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        Assert.Equal(entry.Expected, entry.Read(options));
    }

    public static TheoryData<string> RegistryKeys()
    {
        var data = new TheoryData<string>();
        foreach (var entry in Registry)
            data.Add(entry.Property);
        return data;
    }
}
