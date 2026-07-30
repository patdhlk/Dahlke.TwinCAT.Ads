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
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);

        return new Dictionary<string, string>(
            entries ?? [], StringComparer.OrdinalIgnoreCase);
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
