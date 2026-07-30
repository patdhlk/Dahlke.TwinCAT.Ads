using Microsoft.Extensions.Logging;

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

    [Fact]
    public void KeysDifferingOnlyInCase_Throw_NamingTheFileAndTheKeys()
    {
        // alarms.json is hand-authored and lookup is case-insensitive, so this is a
        // plausible mistake. Dictionary's own "An item with the same key has already been
        // added" names neither the key nor the file, which is useless in a startup log.
        var path = WriteCatalog(
            "alarms.json",
            """{ "BMK1Err404": "Conveyor jam", "bmk1err404": "Förderband blockiert" }""");

        var ex = Assert.Throws<InvalidOperationException>(() => new JsonAlarmTextCatalog(path, null));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
        Assert.Contains("BMK1Err404", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bmk1err404", ex.Message, StringComparison.Ordinal);

        // The framework's own exception is kept as the cause rather than discarded.
        Assert.IsAssignableFrom<ArgumentException>(ex.InnerException);
    }

    [Fact]
    public void KeysDifferingOnlyInCase_InTheLocalizedFile_Throw_NamingThatFile()
    {
        // The localized file goes through the same Load, so it must report itself and not
        // the neutral path the caller passed in.
        WriteCatalog("alarms.json", """{ "BMK1Err404": "Conveyor jam" }""");
        var neutral = Path.Combine(_directory, "alarms.json");
        var localized = WriteCatalog(
            "alarms.de.json",
            """{ "BMK1Err404": "Stau", "BMK1ERR404": "Stau am Förderband" }""");

        var ex = Assert.Throws<InvalidOperationException>(
            () => new JsonAlarmTextCatalog(neutral, null, new System.Globalization.CultureInfo("de")));

        Assert.Contains(localized, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingKey_IsLoggedOncePerKey()
    {
        // The whole point of the miss latch: at a 200 ms cycle, logging every miss emits
        // roughly five identical lines a second for as long as the alarm is outstanding.
        // Every other test here passes logger: null, so a regression to "log every miss"
        // would leave all of them green.
        var path = WriteCatalog("alarms.json", """{ "BMK1Err404": "Conveyor jam" }""");
        var logger = new CapturingLogger();
        var catalog = new JsonAlarmTextCatalog(path, logger);

        catalog.Resolve("BMK9Err999");
        catalog.Resolve("BMK9Err999");
        catalog.Resolve("bmk9err999"); // Same key — the latch is case-insensitive too.
        catalog.Resolve("BMK8Err888"); // A DIFFERENT miss still gets its own line.

        Assert.Equal(2, logger.Messages.Count);
        Assert.Contains(logger.Messages, m => m.Contains("BMK9Err999", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, m => m.Contains("BMK8Err888", StringComparison.Ordinal));

        // A hit never logs at all.
        Assert.Equal("Conveyor jam", catalog.Resolve("BMK1Err404"));
        Assert.Equal(2, logger.Messages.Count);
    }

    /// <summary>
    /// Records every formatted log line. Same shape as <c>PlcAlarmBinderTests</c>'s, typed
    /// to the catalog's own logger so the constructor takes it directly.
    /// </summary>
    private sealed class CapturingLogger : ILogger<JsonAlarmTextCatalog>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
