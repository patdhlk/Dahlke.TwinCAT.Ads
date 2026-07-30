using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Dahlke.TwinCAT.Ads.Alarms.Tests;

/// <summary>
/// Tests for how <see cref="AlarmsServiceCollectionExtensions"/> locates the alarm text
/// catalog.
/// </summary>
/// <remarks>
/// <see cref="JsonAlarmTextCatalog"/> opens the path it is handed, so "which path" is a
/// registration-time decision and belongs here rather than in the catalog's own tests. The
/// relative case is the one that matters: the natural configuration
/// (<c>"TextCatalog": "alarms.json"</c> beside <c>appsettings.json</c>) used to work under
/// <c>dotnet run</c> and fail on a published app, because the process working directory and
/// the content root are the same only by accident.
/// </remarks>
public class AlarmsServiceCollectionExtensionsTests : IDisposable
{
    private const string Key = "BMK1Err404";

    private readonly string _contentRoot =
        Directory.CreateTempSubdirectory("alarm-content-root").FullName;

    private readonly string _elsewhere =
        Directory.CreateTempSubdirectory("alarm-elsewhere").FullName;

    public void Dispose()
    {
        Directory.Delete(_contentRoot, recursive: true);
        Directory.Delete(_elsewhere, recursive: true);
    }

    /// <summary>A minimal <see cref="IHostEnvironment"/> that only has to carry a content root.</summary>
    private sealed class StubHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Dahlke.TwinCAT.Ads.Alarms.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static string WriteCatalog(string directory, string text)
    {
        var path = Path.Combine(directory, "alarms.json");
        File.WriteAllText(path, $$"""{ "{{Key}}": "{{text}}" }""");
        return path;
    }

    /// <summary>
    /// Resolves <see cref="IAlarmTextCatalog"/> out of a container wired exactly as
    /// <c>AddTwinCatAdsAlarms</c> wires it, so this exercises the real registration rather
    /// than a re-implementation of it.
    /// </summary>
    private static IAlarmTextCatalog BuildCatalog(string textCatalog, IHostEnvironment? environment)
    {
        var services = new ServiceCollection();

        if (environment is not null)
            services.AddSingleton(environment);

        services.AddTwinCatAdsAlarms(new ConfigurationBuilder().Build());

        // After AddTwinCatAdsAlarms' own binding delegate, so this is what the options carry.
        services.Configure<PlcAlarmsOptions>(o => o.TextCatalog = textCatalog);

        return services.BuildServiceProvider().GetRequiredService<IAlarmTextCatalog>();
    }

    [Fact]
    public void RelativeTextCatalog_IsResolvedAgainstTheContentRoot()
    {
        WriteCatalog(_contentRoot, "Conveyor jam");

        // Deliberately just the file name: nothing in the process working directory (the
        // test runner's output folder) can satisfy this, so a catalog that resolves the
        // text proves the content root was consulted.
        var catalog = BuildCatalog("alarms.json", new StubHostEnvironment(_contentRoot));

        Assert.Equal("Conveyor jam", catalog.Resolve(Key));
    }

    [Fact]
    public void AbsoluteTextCatalog_IsUsedAsWritten()
    {
        var absolute = WriteCatalog(_elsewhere, "From the absolute path");

        // A DIFFERENT catalog sits at the content root under the same file name. If the
        // registration re-anchored the absolute path, this is the text that would come back.
        WriteCatalog(_contentRoot, "From the content root");

        var catalog = BuildCatalog(absolute, new StubHostEnvironment(_contentRoot));

        Assert.Equal("From the absolute path", catalog.Resolve(Key));
    }

    [Fact]
    public void NoHostEnvironment_StillResolvesTheCatalog()
    {
        // A plain ServiceCollection built without a host has no IHostEnvironment. Resolving
        // it with GetRequiredService would throw here and take the whole registration with
        // it; several existing tests build exactly this container.
        var absolute = WriteCatalog(_elsewhere, "No host needed");

        var catalog = BuildCatalog(absolute, environment: null);

        Assert.Equal("No host needed", catalog.Resolve(Key));
    }

    [Fact]
    public void ResolveCatalogPath_WithoutAnEnvironment_ReturnsThePathUnchanged()
    {
        Assert.Equal(
            "alarms.json",
            AlarmsServiceCollectionExtensions.ResolveCatalogPath("alarms.json", environment: null));
    }

    [Fact]
    public void ResolveCatalogPath_WithABlankContentRoot_ReturnsThePathUnchanged()
    {
        // Path.Combine throws on an empty first segment in some framework versions and
        // silently yields the relative path in others; neither is a resolution, so the
        // blank case is short-circuited rather than combined.
        var environment = new StubHostEnvironment(string.Empty);

        Assert.Equal(
            "alarms.json",
            AlarmsServiceCollectionExtensions.ResolveCatalogPath("alarms.json", environment));
    }

    [Fact]
    public void ResolveCatalogPath_WithARelativePath_CombinesWithTheContentRoot()
    {
        var environment = new StubHostEnvironment(_contentRoot);

        Assert.Equal(
            Path.Combine(_contentRoot, "config/alarms.json"),
            AlarmsServiceCollectionExtensions.ResolveCatalogPath("config/alarms.json", environment));
    }
}
