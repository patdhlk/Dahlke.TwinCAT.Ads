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
    private readonly TimeSpan _shutdownTimeout;
    private readonly IAdsConnectionPool _pool;
    private readonly IAdsRawChannelFactory _rawChannels;
    private readonly ILogger<AdsConnectionPoolHandle> _logger;
    private int _disposed;

    /// <param name="provider">The provider this handle owns and will dispose.</param>
    /// <param name="started">
    /// The hosted services that started successfully, in start order. Stopped in reverse.
    /// </param>
    /// <param name="shutdownTimeout">
    /// The shared budget <see cref="DisposeAsync"/> gives the whole stop loop — see
    /// <see cref="AdsConnectionPoolBuilder.UseShutdownTimeout"/>.
    /// </param>
    internal AdsConnectionPoolHandle(
        ServiceProvider provider,
        IReadOnlyList<IHostedService> started,
        TimeSpan shutdownTimeout)
    {
        _provider = provider;
        _started = started;
        _shutdownTimeout = shutdownTimeout;
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
    /// <b>A hosted service that fails to stop does not throw.</b> It is logged as an error
    /// — through the logger factory supplied via
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
    /// <para>
    /// <b>The final provider disposal is NOT guarded</b>, so this method is not
    /// exception-free: a singleton whose own <c>Dispose</c>/<c>DisposeAsync</c> throws
    /// propagates out of here, and inside an <c>await using</c> it will mask a pending
    /// exception exactly as described above. This is deliberate and matches
    /// <c>Host.DisposeAsync</c>, which disposes its provider unguarded too — a service
    /// that cannot even be disposed is a defect the caller should see, not one to bury in
    /// a log line. The hosted-service stop loop is guarded because a stop failure is an
    /// ordinary, recoverable shutdown condition; a disposal failure is not.
    /// </para>
    /// <para>
    /// <b>Stopping is bounded by one shared timeout</b> — 30 seconds by default, matching
    /// <c>HostOptions.ShutdownTimeout</c>, and set via
    /// <see cref="AdsConnectionPoolBuilder.UseShutdownTimeout"/>. A single
    /// <see cref="CancellationTokenSource"/> is created here, before the loop below runs,
    /// and its token — not a fresh one per service — is passed to every <c>StopAsync</c>,
    /// exactly as <c>Host.StopAsync</c> shares one budget across its own stop loop rather
    /// than resetting it per service. The bound is cooperative, not preemptive: a hosted
    /// service that never observes the token it is given keeps this call waiting exactly
    /// as it always has, which is the same limit a generic host has. A service that DOES
    /// observe the token and is cancelled by it is treated as a stop failure like any
    /// other — logged and stepped over so the remaining services still get their turn and
    /// the provider is still disposed — but logged distinguishably from a service that
    /// threw for some other reason, so a caller reading logs can tell a timeout from a
    /// fault.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        // One CancellationTokenSource for the whole loop, not one per service — the shared
        // budget described in this method's remarks. CancellationTokenSource(TimeSpan)
        // treats Timeout.InfiniteTimeSpan as "never cancel", which is exactly the opt-out
        // UseShutdownTimeout documents.
        using var cts = new CancellationTokenSource(_shutdownTimeout);

        for (var i = _started.Count - 1; i >= 0; i--)
        {
            try
            {
                await _started[i].StopAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token)
            {
                _logger.LogError(
                    "{ServiceType} did not stop within the {ShutdownTimeout} shutdown " +
                    "timeout while disposing the ADS connection pool.",
                    _started[i].GetType().Name,
                    _shutdownTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "{ServiceType} failed to stop while disposing the ADS connection pool.",
                    _started[i].GetType().Name);
            }
        }

        // Unguarded, unlike the stop loop above — see this method's remarks for why, and
        // for the consequence: a throwing singleton Dispose propagates out of here.
        await _provider.DisposeAsync().ConfigureAwait(false);
    }
}
