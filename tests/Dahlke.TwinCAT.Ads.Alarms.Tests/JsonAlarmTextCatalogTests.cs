namespace Dahlke.TwinCAT.Ads.Alarms.Tests;

/// <summary>Unit tests for <see cref="JsonAlarmTextCatalog"/>.</summary>
public class JsonAlarmTextCatalogTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("alarm-catalog-tests").FullName;

    private string WriteCatalog(string fileName, string json)
    {
        var path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Resolve_ReturnsTheMappedText()
    {
        var path = WriteCatalog("alarms.json", """{ "BMK1Err404": "Conveyor jam" }""");
        var catalog = new JsonAlarmTextCatalog(path, null);

        Assert.Equal("Conveyor jam", catalog.Resolve("BMK1Err404"));
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        var path = WriteCatalog("alarms.json", """{ "BMK1Err404": "Conveyor jam" }""");
        var catalog = new JsonAlarmTextCatalog(path, null);

        Assert.Equal("Conveyor jam", catalog.Resolve("bmk1err404"));
    }

    [Fact]
    public void Resolve_UnknownKey_ReturnsNull()
    {
        var path = WriteCatalog("alarms.json", """{ "BMK1Err404": "Conveyor jam" }""");
        var catalog = new JsonAlarmTextCatalog(path, null);

        Assert.Null(catalog.Resolve("BMK9Err999"));
    }

    [Fact]
    public void CultureSpecificFile_TakesPrecedence()
    {
        WriteCatalog("alarms.json", """{ "BMK1Err404": "Conveyor jam" }""");
        var neutral = Path.Combine(_directory, "alarms.json");
        WriteCatalog("alarms.de.json", """{ "BMK1Err404": "Stau am Förderband" }""");

        var catalog = new JsonAlarmTextCatalog(
            neutral, null, new System.Globalization.CultureInfo("de"));

        Assert.Equal("Stau am Förderband", catalog.Resolve("BMK1Err404"));
    }

    [Fact]
    public void CultureSpecificFile_FallsBackToNeutralPerKey()
    {
        WriteCatalog("alarms.json", """{ "BMK1Err404": "Conveyor jam", "BMK2Err1": "Motor fault" }""");
        var neutral = Path.Combine(_directory, "alarms.json");
        WriteCatalog("alarms.de.json", """{ "BMK1Err404": "Stau am Förderband" }""");

        var catalog = new JsonAlarmTextCatalog(
            neutral, null, new System.Globalization.CultureInfo("de"));

        Assert.Equal("Motor fault", catalog.Resolve("BMK2Err1"));
    }

    [Fact]
    public void MissingFile_Throws()
    {
        // A catalog path that does not exist is a configuration error, caught at
        // startup rather than surfacing as every alarm silently losing its text.
        var missing = Path.Combine(_directory, "does-not-exist.json");

        Assert.ThrowsAny<Exception>(() => new JsonAlarmTextCatalog(missing, null));
    }
}
