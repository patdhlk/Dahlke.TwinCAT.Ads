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
/// <b>How it is made to fail.</b> <see cref="EveryBindableOptionIsDeclared"/>
/// reflects over <see cref="TwinCatAdsOptions"/> and compares what it finds against
/// <see cref="Registry"/>. Adding a property without adding a registry entry fails
/// it. The registry is not a list to be rubber-stamped, because
/// <see cref="EveryDeclaredOptionIsReachableFromConfiguration"/> then BINDS each
/// declared path through the real registration path and asserts the value arrives —
/// so satisfying the first test by inventing a plausible-looking entry fails the
/// second, and <c>BindableOption.ReadThrough</c> stops an entry naming one property
/// while reading another. Both are needed: names alone cannot be derived from the
/// binder, since every section has an idiosyncratic layout (a legacy key, a nested
/// re-bind, a single scalar).
/// </para>
/// <para>
/// <b>Its limit, stated rather than implied.</b> This covers the TOP LEVEL of
/// <see cref="TwinCatAdsOptions"/>, which is where a section lives. A new member
/// added to an ALREADY-bound sub-object is not covered — and does not need to be
/// where that sub-object is bound with a whole-section <c>Bind</c>, as
/// <c>RawChannels</c> now is, because <c>Bind</c> picks up new members for free. It
/// WOULD be missed for a sub-object bound property-by-property, which
/// <c>AmsRouterOptions</c> is: only <c>NetId</c> is read. That gap is not closed here;
/// instead the warning sits on the two files someone would actually be editing —
/// <c>AmsRouterOptions</c>'s class doc and the binding line in
/// <c>ServiceCollectionExtensions</c> — since a caveat in a test file nobody opens is
/// how the original defect survived review.
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
    /// <param name="Read">
    /// Reads back what <paramref name="Key"/> should have set, starting from the value
    /// of <paramref name="Property"/> — see <see cref="ReadThrough"/>.
    /// </param>
    /// <param name="Expected">
    /// What <paramref name="Read"/> must return. Deliberately never a default, so a
    /// binder that ignores the key cannot pass by accident.
    /// </param>
    private sealed record BindableOption(
        string Property,
        string Key,
        string Value,
        Func<object, object?> Read,
        object? Expected)
    {
        /// <summary>
        /// Applies <see cref="Read"/> to the value of <see cref="Property"/>, fetched
        /// reflectively by name.
        /// </summary>
        /// <remarks>
        /// The indirection is what ties an entry to the property it CLAIMS to cover.
        /// With a <c>Func&lt;TwinCatAdsOptions, object?&gt;</c> the delegate could
        /// reach anywhere on the options object, so an entry naming a brand-new
        /// property while quietly reading an already-bound one would satisfy both
        /// tests and reinstate the blind spot. Rooting the read at
        /// <see cref="Property"/> makes that impossible rather than merely unlikely.
        /// </remarks>
        internal object? ReadThrough(TwinCatAdsOptions options)
        {
            var property = typeof(TwinCatAdsOptions).GetProperty(Property);
            Assert.NotNull(property);
            var value = property.GetValue(options);
            Assert.NotNull(value);
            return Read(value);
        }
    }

    private static readonly BindableOption[] Registry =
    [
        new("Targets",
            "PlcTargets:probe:AmsNetId", "7.7.7.7.7.7",
            v => ((Dictionary<string, PlcTargetOptions>)v)["probe"].AmsNetId, "7.7.7.7.7.7"),

        new("Router",
            "AmsRouter:NetId", "9.9.9.9.9.9",
            v => ((AmsRouterOptions)v).NetId, "9.9.9.9.9.9"),

        new("Diagnostics",
            "AdsSymbolDump:MaxDepth", "7",
            v => ((AdsDiagnosticsOptions)v).SymbolDump.MaxDepth, 7),

        new("RawChannels",
            "RawChannels:TimeoutMs", "4321",
            v => ((AdsRawChannelOptions)v).TimeoutMs, 4321),
    ];

    /// <summary>
    /// Public instance properties of <see cref="TwinCatAdsOptions"/> that
    /// <c>ConfigurationBinder</c> is capable of populating — the configuration
    /// surface a host can fill in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A public setter is sufficient but NOT necessary, and getting that wrong
    /// blinded this guard to the shape most likely to be written.</b> This filtered on
    /// <c>SetMethod is { IsPublic: true }</c> at first, which silently exempted every
    /// get-only property — and a get-only complex property is fully bindable, because
    /// the binder binds INTO the instance already there rather than assigning a new
    /// one. Options sub-objects are idiomatically written <c>{ get; } = new()</c>, so
    /// the exemption covered precisely the case a maintainer would reach for.
    /// Verified, not assumed: a get-only unbound <c>AmsRouterOptions</c> added to
    /// <see cref="TwinCatAdsOptions"/> left both tests in this file green.
    /// </para>
    /// <para>
    /// A get-only SCALAR is genuinely unbindable — the binder would have to assign it
    /// — so requiring a registry entry for one would be an unsatisfiable demand rather
    /// than a guard. Hence the reference-type-and-non-null condition, which is what
    /// the binder itself needs in order to bind into a property it cannot set.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> DiscoverOptionProperties()
    {
        var probe = new TwinCatAdsOptions();

        return typeof(TwinCatAdsOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && IsPopulatedByTheBinder(p, probe))
            .Select(p => p.Name);
    }

    private static bool IsPopulatedByTheBinder(PropertyInfo property, TwinCatAdsOptions probe) =>
        property.SetMethod is { IsPublic: true }
        // Get-only: bindable only if the binder can bind into an existing instance.
        || (property.PropertyType.IsClass
            && property.PropertyType != typeof(string)
            && property.GetValue(probe) is not null);

    [Fact]
    public void EveryBindableOptionIsDeclared()
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

        Assert.Equal(entry.Expected, entry.ReadThrough(options));
    }

    public static TheoryData<string> RegistryKeys()
    {
        var data = new TheoryData<string>();
        foreach (var entry in Registry)
            data.Add(entry.Property);
        return data;
    }
}
