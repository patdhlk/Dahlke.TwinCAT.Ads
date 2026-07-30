namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// Resolves an alarm's <c>sKey</c> to human-readable text.
/// </summary>
/// <remarks>
/// Register your own implementation before calling <c>AddTwinCatAdsAlarms</c> to source
/// text from a database, a resource assembly, or anywhere else; the built-in JSON
/// catalog is only registered when none is present.
/// </remarks>
public interface IAlarmTextCatalog
{
    /// <summary>
    /// Returns the text for <paramref name="alarmKey"/>, or <see langword="null"/> when
    /// the key is unknown. Must be safe to call concurrently.
    /// </summary>
    string? Resolve(string alarmKey);
}
