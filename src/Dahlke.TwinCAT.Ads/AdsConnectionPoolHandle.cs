using System.Diagnostics.CodeAnalysis;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// A started, self-owned connection pool obtained from
/// <see cref="AdsConnectionPoolBuilder.BuildAndStartAsync"/>. Implements
/// <see cref="IAdsConnectionPool"/>, so it is used exactly like the pool a hosted
/// application resolves from DI, and owns the lifetime a generic host would otherwise own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dispose it.</b> Until <see cref="DisposeAsync"/> runs, the embedded router, the
/// per-target reconnect loops and the raw-channel transports are all live. The intended
/// shape is <c>await using</c>.
/// </para>
/// <para>
/// <see cref="IAsyncDisposable"/> and NOT <see cref="IDisposable"/>: stopping hosted
/// services is genuinely asynchronous, and a synchronous <c>Dispose</c> would either
/// block or skip the stop. A WPF or WinForms shutdown path that cannot <c>await</c> can
/// call <c>DisposeAsync().AsTask().GetAwaiter().GetResult()</c> — explicitly, rather than
/// having this type hide the same block behind a method that looks free.
/// </para>
/// </remarks>
public sealed class AdsConnectionPoolHandle : IAdsConnectionPool, IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IReadOnlyList<IHostedService> _started;
    private readonly IAdsConnectionPool _pool;
    private int _disposed;

    /// <param name="provider">The provider this handle owns and will dispose.</param>
    /// <param name="started">
    /// The hosted services that started successfully, in start order. Stopped in reverse.
    /// </param>
    internal AdsConnectionPoolHandle(ServiceProvider provider, IReadOnlyList<IHostedService> started)
    {
        _provider = provider;
        _started = started;
        _pool = provider.GetRequiredService<IAdsConnectionPool>();
    }

    /// <summary>
    /// The service provider backing this pool. The escape hatch for anything registered
    /// through <see cref="AdsConnectionPoolBuilder.ConfigureServices"/> — for example the
    /// alarm monitor from <c>Dahlke.TwinCAT.Ads.Alarms</c>.
    /// </summary>
    public IServiceProvider Services => _provider;

    /// <summary>
    /// The raw ADS channel factory, for targets the symbol API cannot reach.
    /// </summary>
    public IAdsRawChannelFactory RawChannels => _provider.GetRequiredService<IAdsRawChannelFactory>();

    /// <inheritdoc/>
    public IAdsConnection GetConnection(string plcId) => _pool.GetConnection(plcId);

    /// <inheritdoc/>
    public bool TryGetConnection(string plcId, [NotNullWhen(true)] out IAdsConnection? connection)
        => _pool.TryGetConnection(plcId, out connection);

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, IAdsConnection> GetAllConnections() => _pool.GetAllConnections();

    /// <inheritdoc/>
    public void ForceReconnect(string plcId) => _pool.ForceReconnect(plcId);

    /// <inheritdoc/>
    public IReadOnlyList<PlcTargetStatus> GetTargetStates() => _pool.GetTargetStates();

    /// <inheritdoc/>
    public bool TryGetSimulatedConnection(string plcId, [NotNullWhen(true)] out SimulatedAdsConnection? simulated)
        => _pool.TryGetSimulatedConnection(plcId, out simulated);

    /// <summary>
    /// Stops every hosted service in reverse start order, then disposes the provider.
    /// Idempotent — a second call is a no-op.
    /// </summary>
    /// <remarks>
    /// A service that throws on stop does not prevent the remaining ones from stopping or
    /// the provider from being disposed; the failures are collected and rethrown together
    /// afterwards, so a botched shutdown is neither silent nor a resource leak.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        List<Exception>? failures = null;

        for (var i = _started.Count - 1; i >= 0; i--)
        {
            try
            {
                await _started[i].StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        await _provider.DisposeAsync().ConfigureAwait(false);

        if (failures is not null)
            throw new AggregateException(
                "One or more services failed to stop while disposing the ADS connection pool.",
                failures);
    }
}
