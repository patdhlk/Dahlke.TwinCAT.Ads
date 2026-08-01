using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// The full-subtree browse moved from <c>GetSymbolsAsync(parentPath, ct)</c> to
/// <see cref="IAdsConnection.GetSymbolTreeAsync"/>, and the old overload is deprecated rather
/// than repurposed.
///
/// That distinction is the whole point, so it is what these tests pin. Flipping the old overload
/// to mean one level would have left every call site compiling while changing what it did; a
/// deprecation cannot, because the old call keeps its exact behaviour until it is deleted.
///
/// Coverage:
/// - `GetSymbolTreeAsync` projects the full subtree, and the explicit three-argument overload
///   with `includeChildren: false` still projects one level.
/// - The deprecated overload behaves IDENTICALLY to the new name — same results, not merely a
///   compiling call. This is what makes the change safe to take.
/// - The deprecation is real (the attribute is present) and its message names both replacements,
///   so the compiler warning tells a consumer what to write instead rather than only what to stop
///   writing.
/// </summary>
public class GetSymbolTreeDeprecationTests
{
    private static SimulatedAdsConnection CreateSeededSim()
    {
        var sim = new SimulatedAdsConnection("plc1", "PLC One", NullLoggerFactory.Instance);
        sim.SetInitialValues(new Dictionary<string, object?>
        {
            ["MAIN.Motor.Speed"] = 1500,
            ["MAIN.Motor.Running"] = true,
            ["MAIN.Counter"] = 7,
        });
        return sim;
    }

    // =========================================================================
    // The two shapes do what their names say
    // =========================================================================

    [Fact]
    public async Task GetSymbolTreeAsync_ProjectsTheWholeSubtree()
    {
        using var sim = CreateSeededSim();
        IAdsConnection conn = sim;

        var roots = await conn.GetSymbolTreeAsync(null);

        var main = Assert.Single(roots);
        Assert.Equal("MAIN", main.InstancePath);
        Assert.NotNull(main.Children);

        // Recursively, all the way down — MAIN.Motor is itself populated.
        var motor = Assert.Single(main.Children!, c => c.InstancePath == "MAIN.Motor");
        Assert.NotNull(motor.Children);
        Assert.Contains(motor.Children!, c => c.InstancePath == "MAIN.Motor.Speed");
    }

    [Fact]
    public async Task GetSymbolsAsync_WithIncludeChildrenFalse_StillProjectsOneLevel()
    {
        using var sim = CreateSeededSim();
        IAdsConnection conn = sim;

        var roots = await conn.GetSymbolsAsync(null, includeChildren: false);

        Assert.All(roots, s => Assert.Null(s.Children));
    }

    // =========================================================================
    // The deprecated overload is unchanged, not repurposed
    // =========================================================================

    [Fact]
    public async Task DeprecatedOverload_ReturnsExactlyWhatGetSymbolTreeAsyncReturns()
    {
        using var sim = CreateSeededSim();
        IAdsConnection conn = sim;

#pragma warning disable CS0618 // deliberately calling the deprecated overload: that IS the test
        var deprecated = await conn.GetSymbolsAsync(null);
#pragma warning restore CS0618
        var renamed = await conn.GetSymbolTreeAsync(null);

        // Same shape and same depth — the old call did not quietly become a one-level browse.
        Assert.Equal(Flatten(renamed), Flatten(deprecated));
        Assert.Contains("MAIN.Motor.Speed", Flatten(deprecated));

        static List<string> Flatten(IReadOnlyList<AdsSymbolInfo> symbols)
        {
            var paths = new List<string>();
            void Walk(IReadOnlyList<AdsSymbolInfo> level)
            {
                foreach (var s in level.OrderBy(s => s.InstancePath, StringComparer.Ordinal))
                {
                    paths.Add(s.InstancePath);
                    if (s.Children is { } children) Walk(children);
                }
            }
            Walk(symbols);
            return paths;
        }
    }

    [Fact]
    public async Task DeprecatedOverload_OnTheSimulatedConcreteType_IsAlsoUnchanged()
    {
        using var sim = CreateSeededSim();

#pragma warning disable CS0618
        var deprecated = await sim.GetSymbolsAsync(null);
#pragma warning restore CS0618

        Assert.NotNull(Assert.Single(deprecated).Children);
    }

    // =========================================================================
    // The deprecation itself
    // =========================================================================

    [Fact]
    public void DeprecatedOverload_CarriesObsoleteNamingBothReplacements()
    {
        var method = typeof(IAdsConnection)
            .GetMethods()
            .Single(m => m.Name == nameof(IAdsConnection.GetSymbolsAsync)
                      && m.GetParameters().Length == 2
                      && m.GetParameters()[1].ParameterType == typeof(CancellationToken));

        var obsolete = method.GetCustomAttribute<ObsoleteAttribute>();

        Assert.NotNull(obsolete);

        // A warning that only says "stop doing this" makes the consumer go read the source. Both
        // replacements are named because which one they want depends on what they meant.
        Assert.Contains("GetSymbolTreeAsync", obsolete!.Message);
        Assert.Contains("includeChildren: false", obsolete.Message);

        // A warning, not an error: existing code keeps building through the deprecation window.
        Assert.False(obsolete.IsError);
    }

    [Fact]
    public void GetSymbolTreeAsync_IsNotItselfDeprecated()
    {
        var method = typeof(IAdsConnection).GetMethod(nameof(IAdsConnection.GetSymbolTreeAsync));

        Assert.NotNull(method);
        Assert.Null(method!.GetCustomAttribute<ObsoleteAttribute>());
    }

    [Fact]
    public void ThreeArgumentOverload_IsNotDeprecated_ItIsTheReplacementForOneLevelBrowsing()
    {
        var method = typeof(IAdsConnection)
            .GetMethods()
            .Single(m => m.Name == nameof(IAdsConnection.GetSymbolsAsync)
                      && m.GetParameters().Length == 3);

        Assert.Null(method.GetCustomAttribute<ObsoleteAttribute>());
    }
}
