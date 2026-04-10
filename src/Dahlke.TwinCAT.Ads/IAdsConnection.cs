using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

public interface IAdsConnection
{
    string PlcId { get; }
    string DisplayName { get; }
    bool IsConnected { get; }

    Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct);
    Task WriteValueAsync(string symbolPath, object value, CancellationToken ct);
    Task<Dictionary<string, object?>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct);
    Task WriteValuesAsync(Dictionary<string, object> values, CancellationToken ct);

    Task<AdsState> GetAdsStateAsync(CancellationToken ct);
    Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct);
}
