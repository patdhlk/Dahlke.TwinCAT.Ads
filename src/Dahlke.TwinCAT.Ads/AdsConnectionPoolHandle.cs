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
    private readonly IAdsRawChannelFactory _rawChannels;
    private readonly ILogger<AdsConnectionPoolHandle> _logger;
    private int _disposed;

    /// <param name="provider">The provider this handle owns and will dispose.</param>
    /// <param name="started">
    /// The hosted services that started successfully, in start order. Stopped in reverse.
    /// </param>
    internal AdsConnectionPoolHandle(ServiceProvider provider, IReadOnlyList<IHostedService> started)
    {
        _provider = provider;
        _started = started;
        // Both are singletons already constructed by the time this ctor runs — resolved
        // once here, like _pool, rather than per-get, so RawChannels and GetConnection
        // share the same post-disposal failure mode.
        _pool = provider.GetRequiredService<IAdsConnectionPool>();
        _rawChannels = provider.GetRequiredService<IAdsRawChannelFactory>();
        _logger = provider.GetRequiredService<ILogger<AdsConnectionPoolHandle>>();
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
    public IAdsRawChannelFactory RawChannels => _rawChannels;

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
    /// <para>
    /// <b>Never throws.</b> A service that fails to stop is logged as an error — through
    /// the logger factory supplied via
    /// <see cref="AdsConnectionPoolBuilder.UseLoggerFactory"/>, or silently swallowed by
    /// the null logger if none was supplied — rather than thrown; the remaining services
    /// still stop, in order, and the provider is still disposed either way.
    /// </para>
    /// <para>
    /// <c>await using</c> lowers to a <c>try/finally</c>, and an exception thrown from a
    /// <c>finally</c> block REPLACES whatever exception was already propagating out of the
    /// <c>try</c>. A body that fails and then hits a botched shutdown on the way out would
    /// therefore surface the shutdown failure and lose the real one — the fault the caller
    /// actually needs to see. The generic host makes the same choice for the same reason:
    /// <c>Host.StopAsync</c> may throw; <c>Host.DisposeAsync</c> does not.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        for (var i = _started.Count - 1; i >= 0; i--)
        {
            try
            {
                await _started[i].StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "{ServiceType} failed to stop while disposing the ADS connection pool.",
                    _started[i].GetType().Name);
            }
        }

        await _provider.DisposeAsync().ConfigureAwait(false);
    }
}
