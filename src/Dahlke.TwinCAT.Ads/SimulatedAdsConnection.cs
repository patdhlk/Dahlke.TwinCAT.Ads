using System.Collections.Concurrent;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// In-memory simulated PLC connection for offline development and testing.
/// Stores values in a thread-safe dictionary — written values are returned on subsequent reads.
/// </summary>
public sealed class SimulatedAdsConnection : IManagedConnection
{
    private readonly ILogger<SimulatedAdsConnection> _logger;
    private readonly ConcurrentDictionary<string, object?> _symbols = new();

    public string PlcId { get; }
    public string DisplayName { get; }
    public bool IsConnected => true;

    /// <inheritdoc />
    /// <remarks>
    /// A simulated connection is permanently connected; this property always
    /// returns <see cref="ConnectionState.Connected"/>.
    /// </remarks>
    public ConnectionState State => ConnectionState.Connected;

    /// <inheritdoc />
    /// <remarks>
    /// A simulated connection has no lifecycle transitions — it is always
    /// <see cref="ConnectionState.Connected"/> — so this event is never raised.
    /// Subscribing is harmless. When consumers hold the facade returned by
    /// <see cref="IAdsConnectionPool.GetConnection"/> (the normal case) the
    /// facade's own <c>ConnectionStateChanged</c> reports pool-driven transitions
    /// instead; the direct-<c>SimulatedAdsConnection</c> case is mainly for
    /// unit tests and the C26 adapter.
    /// </remarks>
#pragma warning disable CS0067 // The event is never used — by design; see remarks.
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
#pragma warning restore CS0067

    public SimulatedAdsConnection(string plcId, string displayName, ILoggerFactory loggerFactory)
    {
        PlcId = plcId;
        DisplayName = displayName;
        _logger = loggerFactory.CreateLogger<SimulatedAdsConnection>();
        _logger.LogInformation("Simulated ADS connection {PlcId} ({DisplayName}) started", plcId, displayName);
    }

    /// <summary>
    /// Pre-populates the simulated symbol store with initial values.
    /// Useful for setting up test fixtures or default state.
    /// </summary>
    public void SetInitialValues(IReadOnlyDictionary<string, object?> values)
    {
        foreach (var (key, value) in values)
            _symbols[key] = value;
    }

    public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct)
    {
        _symbols.TryGetValue(symbolPath, out var value);
        return Task.FromResult(value);
    }

    public Task WriteValueAsync(string symbolPath, object value, CancellationToken ct)
    {
        _symbols[symbolPath] = value;
        return Task.CompletedTask;
    }

    public async Task<Dictionary<string, object?>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct)
    {
        var results = new Dictionary<string, object?>();
        foreach (var path in symbolPaths)
            results[path] = (await ReadValueAsync(path, ct).ConfigureAwait(false));
        return results;
    }

    public Task WriteValuesAsync(Dictionary<string, object> values, CancellationToken ct)
    {
        foreach (var (path, value) in values)
            _symbols[path] = value;
        return Task.CompletedTask;
    }

    public Task<AdsState> GetAdsStateAsync(CancellationToken ct)
        => Task.FromResult(AdsState.Run);

    public Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct)
        => Task.FromResult<IDisposable>(NoOpDisposable.Instance);

    void IManagedConnection.Connect() { }
    void IManagedConnection.Disconnect() { }
    Task<bool> IManagedConnection.IsAliveAsync(CancellationToken ct) => Task.FromResult(true);
    void IManagedConnection.ForceDisconnect() { }
    void IManagedConnection.LogSymbolTree(SymbolDumpOptions options) { }

    public void Dispose() { }

    private sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }
}
