using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>An <see cref="IAlarmTextCatalog"/> that never resolves anything.</summary>
internal sealed class NullAlarmTextCatalog : IAlarmTextCatalog
{
    public static readonly NullAlarmTextCatalog Instance = new();

    private NullAlarmTextCatalog()
    {
    }

    public string? Resolve(string alarmKey) => null;
}

/// <summary>
/// An <see cref="IAlarmTextCatalog"/> backed by one or two JSON files mapping
/// <c>sKey</c> to text.
/// </summary>
/// <remarks>
/// <para>
/// Given <c>alarms.json</c> and a current culture of <c>de</c>, a sibling
/// <c>alarms.de.json</c> is loaded and consulted first, falling back to the neutral
/// file PER KEY so a partial translation degrades to the neutral text rather than to
/// nothing.
/// </para>
/// <para>
/// <b>Missing keys are logged once per key.</b> A notification cycle of 200 ms would
/// otherwise emit five identical log lines a second for as long as the alarm is
/// outstanding.
/// </para>
/// </remarks>
internal sealed class JsonAlarmTextCatalog : IAlarmTextCatalog
{
    private readonly Dictionary<string, string> _neutral;
    private readonly Dictionary<string, string> _localized;
    private readonly ConcurrentDictionary<string, byte> _reportedMisses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<JsonAlarmTextCatalog>? _logger;

    public JsonAlarmTextCatalog(
        string path, ILogger<JsonAlarmTextCatalog>? logger, CultureInfo? culture = null)
    {
        _logger = logger;
        _neutral = Load(path);
        _localized = LoadLocalized(path, culture ?? CultureInfo.CurrentUICulture);
    }

    /// <inheritdoc />
    public string? Resolve(string alarmKey)
    {
        if (_localized.TryGetValue(alarmKey, out var localized))
            return localized;

        if (_neutral.TryGetValue(alarmKey, out var neutral))
            return neutral;

        if (_reportedMisses.TryAdd(alarmKey, 0))
        {
            _logger?.LogInformation(
                "Alarm key {AlarmKey} has no entry in the alarm text catalog; the alarm is " +
                "reported without text. This is logged once per key.", alarmKey);
        }

        return null;
    }

    private static Dictionary<string, string> Load(string path)
    {
        // Deliberately unguarded: a configured catalog path that cannot be read is a
        // configuration error worth failing startup for, not a reason to silently
        // strip the text from every alarm.
        using var stream = File.OpenRead(path);
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];

        try
        {
            return new Dictionary<string, string>(entries, StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException ex)
        {
            // Two keys differing only in case. The file is hand-authored and lookup here
            // is case-insensitive, so this is a plausible mistake — but Dictionary's own
            // message ("An item with the same key has already been added") names neither
            // the key nor the file, which is worthless in a service's startup log. Say
            // both. The collision is only searched for on this path, so the good case
            // still costs one dictionary copy and nothing else.
            throw new InvalidOperationException(DuplicateKeyMessage(path, entries), ex);
        }
    }

    /// <summary>
    /// Names the file and every set of keys that collide case-insensitively, for the
    /// diagnostic that replaces <see cref="Dictionary{TKey, TValue}"/>'s bare
    /// <see cref="ArgumentException"/>.
    /// </summary>
    private static string DuplicateKeyMessage(string path, Dictionary<string, string> entries)
    {
        // entries still carries BOTH spellings: it comes back from the deserializer with
        // the default ordinal comparer, which is exactly why the copy above is where the
        // collision first shows up.
        var collisions = entries.Keys
            .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(" / ", group.Select(key => $"'{key}'")))
            .ToList();

        var detail = collisions.Count > 0
            ? $"Colliding keys: {string.Join("; ", collisions)}."
            : "The colliding key could not be identified.";

        return
            $"The alarm text catalog '{path}' has entries whose keys differ only in case. " +
            $"Alarm keys are matched case-insensitively, so only one spelling can be kept — " +
            $"remove or merge the duplicates. {detail}";
    }

    private static Dictionary<string, string> LoadLocalized(string path, CultureInfo culture)
    {
        foreach (var name in CultureCandidates(culture))
        {
            var candidate = Path.Combine(
                Path.GetDirectoryName(path) ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(path)}.{name}{Path.GetExtension(path)}");

            if (File.Exists(candidate))
                return Load(candidate);
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> CultureCandidates(CultureInfo culture)
    {
        for (var current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
            yield return current.Name;
    }
}
