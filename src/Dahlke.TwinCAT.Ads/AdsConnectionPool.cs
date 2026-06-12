using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads;

internal sealed class AdsConnectionPool : IHostedService, IAdsConnectionPool, IDisposable
{
    private readonly Dictionary<string, PlcTargetOptions> _targets;
    private readonly SymbolDumpOptions _symbolDump;
    private readonly IAdsConnectionFactory _connectionFactory;
    private readonly AdsRouterReadySignal _readySignal;
    private readonly ILogger<AdsConnectionPool> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, IManagedConnection> _connections = new(StringComparer.OrdinalIgnoreCase);

    // Stable per-target facades. Created eagerly in StartAsync — one per
    // CONFIGURED target, independent of whether (or when) that target's loop ever
    // connects. A facade's identity never changes for the pool's lifetime; the
    // loop pushes the live underlying connection into it via SetCurrent and clears
    // it via ClearCurrent at exactly the points it updates _connections.
    private readonly ConcurrentDictionary<string, AdsConnectionFacade> _facades = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _reconnectCts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _loopTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConnectionState> _states = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _stoppingCts;

    /// <summary>
    /// Raised on every connection-state transition for any target.
    /// </summary>
    /// <remarks>
    /// Handlers are invoked synchronously from the background reconnect loop
    /// thread (not the thread that started the pool). They must be thread-safe
    /// and should not block. Any exception a handler throws is caught and logged
    /// by the pool and will not interrupt reconnection.
    /// <para>
    /// When a handler observes the transition into
    /// <see cref="ConnectionState.Disconnected"/> for a target, the dead
    /// underlying connection has already been removed from the pool and cleared
    /// from that target's facade. <see cref="GetConnection"/> still returns the
    /// stable facade (its identity never changes), but the facade then reports
    /// <see cref="IAdsConnection.IsConnected"/> as <see langword="false"/> and any
    /// operation on it throws <see cref="AdsConnectionUnavailableException"/> — a
    /// handler is never routed to an about-to-be-disposed connection.
    /// </para>
    /// </remarks>
    internal event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    private static readonly TimeSpan MinReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposeGracePeriod = TimeSpan.FromSeconds(2);

    public AdsConnectionPool(
        IOptions<TwinCatAdsOptions> options,
        IAdsConnectionFactory connectionFactory,
        AdsRouterReadySignal readySignal,
        ILogger<AdsConnectionPool> logger,
        TimeProvider timeProvider)
    {
        var value = options.Value;
        _targets = value.Targets;
        _symbolDump = value.Diagnostics.SymbolDump;
        _connectionFactory = connectionFactory;
        _readySignal = readySignal;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Eagerly create one stable facade per CONFIGURED target — including real
        // targets whose loops may be skipped on router failure, and before any
        // loop has connected. GetConnection therefore returns a non-changing
        // facade from this point on; operations on it throw
        // AdsConnectionUnavailableException until a connection is published.
        foreach (var (plcId, options) in _targets)
            _facades[plcId] = new AdsConnectionFacade(plcId, options, _timeProvider);

        var hasRealTargets = _targets.Values.Any(t => t.Mode == ConnectionMode.Real);

        if (!hasRealTargets)
        {
            // All targets are simulated — no router is needed; start loops immediately.
            _logger.LogInformation(
                "ADS connection pool starting — all {Count} target(s) are simulated, skipping router wait",
                _targets.Count);

            foreach (var (plcId, options) in _targets)
                StartConnectionLoop(plcId, options);

            return;
        }

        _logger.LogInformation("ADS connection pool starting, waiting for router...");
        try
        {
            await _readySignal.WaitAsync(_stoppingCts.Token).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // Router failed or was cancelled: start loops only for simulated targets;
            // real targets are skipped (they require a working router).
            var simTargets = _targets
                .Where(kvp => kvp.Value.Mode == ConnectionMode.Simulated)
                .ToList();
            var realTargets = _targets
                .Where(kvp => kvp.Value.Mode == ConnectionMode.Real)
                .ToList();

            if (simTargets.Count > 0)
            {
                _logger.LogWarning(
                    "ADS router not available — starting {SimCount} simulated target(s); " +
                    "skipping {RealCount} real target(s): {RealIds}",
                    simTargets.Count,
                    realTargets.Count,
                    string.Join(", ", realTargets.Select(kvp => kvp.Key)));

                foreach (var (plcId, options) in simTargets)
                    StartConnectionLoop(plcId, options);
            }
            else
            {
                _logger.LogWarning(
                    "ADS router not available — connection pool running without connections " +
                    "({RealCount} real target(s) skipped: {RealIds})",
                    realTargets.Count,
                    string.Join(", ", realTargets.Select(kvp => kvp.Key)));
            }

            return;
        }

        _logger.LogInformation("ADS router ready, connecting to {Count} PLC target(s)", _targets.Count);

        foreach (var (plcId, options) in _targets)
        {
            StartConnectionLoop(plcId, options);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ADS connection pool stopping...");

        // Cancel background loops
        foreach (var (_, cts) in _reconnectCts)
            cts.Cancel();

        // Wait for background loops to finish
        var loopTasks = _loopTasks.Values.ToArray();
        if (loopTasks.Length > 0)
        {
            try { await Task.WhenAll(loopTasks).WaitAsync(TimeSpan.FromSeconds(10), _timeProvider, cancellationToken); }
            catch { /* Timeout or cancellation — clean up anyway */ }
        }

        foreach (var (_, cts) in _reconnectCts)
            cts.Dispose();
        _reconnectCts.Clear();
        _loopTasks.Clear();

        // Mark every facade stopped first so that, once the pool is stopping, all
        // operations fail FAST with AdsConnectionUnavailableException — both new
        // calls and any already parked waiting for a reconnection that will never
        // come — rather than waiting out TimeoutMs or routing to a connection we
        // are about to dispose. MarkStopped also clears the current pointer's
        // effect (a stopped facade never serves a connection). Facades hold no
        // resources, so they are not disposed — the (now inert) instances are
        // retained so GetConnection keeps returning a stable identity.
        foreach (var (_, facade) in _facades)
            facade.MarkStopped();

        foreach (var (plcId, connection) in _connections)
        {
            try { connection.Disconnect(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disconnecting from PLC {PlcId}", plcId); }
            connection.Dispose();
            SetState(plcId, ConnectionState.Disconnected);
        }

        // Any target whose loop was torn down mid-attempt (e.g. cancelled while
        // Connecting, before it ever published a connection) must also settle on
        // Disconnected so GetState and the final event reflect the stopped pool.
        foreach (var plcId in _states.Keys)
            SetState(plcId, ConnectionState.Disconnected);

        _connections.Clear();
        _stoppingCts?.Cancel();
        _stoppingCts?.Dispose();
        _stoppingCts = null;
    }

    public IAdsConnection? GetConnection(string plcId)
    {
        // Returns the stable per-target facade. Null only for an UNCONFIGURED id
        // (no facade was created for it). For a configured target the facade is
        // returned even before its loop connects or during an outage — operations
        // on it then throw AdsConnectionUnavailableException.
        _facades.TryGetValue(plcId, out var facade);
        return facade;
    }

    public IReadOnlyDictionary<string, IAdsConnection> GetAllConnections()
    {
        return _facades.ToDictionary(kvp => kvp.Key, kvp => (IAdsConnection)kvp.Value);
    }

    /// <summary>
    /// Returns the current <see cref="ConnectionState"/> for the given target,
    /// or <see cref="ConnectionState.Disconnected"/> for an unknown identifier.
    /// </summary>
    internal ConnectionState GetState(string plcId)
        => _states.TryGetValue(plcId, out var state) ? state : ConnectionState.Disconnected;

    /// <summary>
    /// Swaps the tracked state for <paramref name="plcId"/> to
    /// <paramref name="next"/> and raises <see cref="ConnectionStateChanged"/>
    /// only when the state actually changes.
    /// </summary>
    /// <remarks>
    /// Synchronous and allocation-light: it performs no awaits, so it can be
    /// called from inside the reconnect loop without altering its timing. The
    /// state swap and the change comparison use the previous value read back
    /// from the dictionary, so the <c>PreviousState</c> reported is the value
    /// observed at swap time. Handler exceptions are caught and logged here so a
    /// faulty subscriber can never tear down the loop.
    /// </remarks>
    private void SetState(string plcId, ConnectionState next)
    {
        // Capture the prior value atomically with the swap. The update factory
        // records what we displaced; the add factory means "no prior entry", in
        // which case the implicit start is Disconnected.
        var previous = ConnectionState.Disconnected;
        _states.AddOrUpdate(
            plcId,
            _ => next,
            (_, existing) =>
            {
                previous = existing;
                return next;
            });

        if (previous == next)
            return;

        var handlers = ConnectionStateChanged;
        if (handlers is null)
            return;

        try
        {
            handlers(this, new ConnectionStateChangedEventArgs(plcId, next, previous));
        }
        catch (Exception ex)
        {
            // A subscriber's exception must never propagate into the loop.
            _logger.LogWarning(
                ex,
                "ConnectionStateChanged handler threw while reporting {PlcId} -> {State}",
                plcId,
                next);
        }
    }

    public void ForceReconnect(string plcId)
    {
        if (!_targets.TryGetValue(plcId, out var options))
        {
            _logger.LogWarning("ForceReconnect: PLC {PlcId} not configured", plcId);
            return;
        }

        // A simulated target retains its runtime state in memory — replacing the
        // connection would wipe values written since startup, which diverges from
        // real PLC behavior (a real PLC retains its variables across client
        // reconnects).  Log and return without touching the loop.
        if (options.Mode == ConnectionMode.Simulated)
        {
            _logger.LogInformation(
                "ForceReconnect: simulated target {PlcId} — reconnect is a no-op", plcId);
            return;
        }

        _logger.LogInformation("ForceReconnect: forcing reconnection to PLC {PlcId}", plcId);

        // Cancel old loop
        if (_reconnectCts.TryRemove(plcId, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        // Capture the old loop task before StartConnectionLoop overwrites the
        // _loopTasks entry. The cancelled old loop exits promptly; the new loop
        // awaits it before its first connect attempt so the old connection's
        // Disconnected transition is published before the new Connecting/
        // Connected — and StopAsync waits on BOTH, so the old loop is never
        // orphaned.
        _loopTasks.TryRemove(plcId, out var oldTask);

        // Start new loop (registers the new task under _loopTasks[plcId]),
        // gated on the old loop finishing its teardown.
        StartConnectionLoop(plcId, options, oldTask);

        if (oldTask is not null && _loopTasks.TryGetValue(plcId, out var newTask))
            _loopTasks[plcId] = Task.WhenAll(oldTask, newTask);
    }

    public void Dispose()
    {
        foreach (var (_, cts) in _reconnectCts)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
        // Mark facades stopped before disposing the underlying connections so no
        // facade routes to a disposed instance and any parked waiters fail fast
        // (mirrors StopAsync).
        foreach (var (_, facade) in _facades)
            facade.MarkStopped();
        foreach (var (_, connection) in _connections)
            connection.Dispose();
        _stoppingCts?.Dispose();
    }

    /// <summary>
    /// Connection loop: connects, periodically checks health,
    /// and rebuilds connection on failure.
    /// </summary>
    /// <remarks>
    /// This loop is connection-mode agnostic — it needs no special-casing for
    /// <see cref="ConnectionMode.Simulated"/> targets. The factory decides which
    /// concrete connection to build; a simulated connection's <c>Connect()</c> is
    /// a no-op that succeeds instantly and its <c>IsAliveAsync</c> always returns
    /// true, so a simulated target connects immediately and never reconnect-churns.
    /// </remarks>
    private void StartConnectionLoop(
        string plcId, PlcTargetOptions options, Task? predecessor = null)
    {
        var cts = new CancellationTokenSource();
        _reconnectCts[plcId] = cts;

        _loopTasks[plcId] = Task.Run(async () =>
        {
            // When replacing an existing loop (ForceReconnect), wait for it to
            // finish tearing down its connection first. This serialises the old
            // connection's Disconnected transition ahead of this loop's
            // Connecting/Connected, and guarantees the old connection is fully
            // removed before we publish ours. The predecessor loop never faults,
            // but guard defensively so a torn-down old loop can't kill the new one.
            if (predecessor is not null)
            {
                try { await predecessor.ConfigureAwait(false); }
                catch { /* old loop teardown errors are already logged by it */ }
            }

            var delay = MinReconnectDelay;

            while (!cts.Token.IsCancellationRequested)
            {
                IManagedConnection? ads = null;
                var cancelled = false;
                var published = false;
                try
                {
                    // Create new connection
                    ads = _connectionFactory.Create(plcId, options);
                    _logger.LogInformation("Connecting to PLC {PlcId}...", plcId);
                    SetState(plcId, ConnectionState.Connecting);
                    ads.Connect();
                    // Re-check after the synchronous Connect(): if StopAsync
                    // cancelled while Connect() was blocking (possibly past the
                    // 10s teardown timeout), publishing now would resurrect a
                    // stopped pool's facade pointer. Throwing routes through the
                    // cancellation catch into the cleanup block, which tears the
                    // connection down without ever publishing it.
                    cts.Token.ThrowIfCancellationRequested();
                    _connections[plcId] = ads;
                    // Publish the live connection into the stable facade so
                    // GetConnection's facade now routes here. Piggybacks the
                    // _connections write — same point, same instance.
                    if (_facades.TryGetValue(plcId, out var facadeToSet))
                        facadeToSet.SetCurrent(ads);
                    published = true;
                    SetState(plcId, ConnectionState.Connected);
                    delay = MinReconnectDelay;

                    _logger.LogInformation("PLC {PlcId} connected, starting health check", plcId);

                    // Log symbol tree if enabled in options
                    if (_symbolDump.Enabled)
                        ads.LogSymbolTree(_symbolDump);

                    // Health check loop: checks if connection is still alive
                    while (!cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(HealthCheckInterval, _timeProvider, cts.Token).ConfigureAwait(false);

                        if (!await ads.IsAliveAsync(cts.Token).ConfigureAwait(false))
                        {
                            _logger.LogWarning("PLC {PlcId}: health check failed, reconnecting...", plcId);
                            break; // Exit inner loop -> reconnect
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
                {
                    // Cancellation (StopAsync or ForceReconnect). Fall through to
                    // the cleanup block so a live connection still emits
                    // Disconnected and is torn down — never break here.
                    cancelled = true;
                }
                catch (Exception ex)
                {
                    if (cts.Token.IsCancellationRequested)
                        cancelled = true;
                    else
                        _logger.LogWarning("PLC {PlcId}: connection error: {Message}, retrying in {Delay}s",
                            plcId, ex.Message, delay.TotalSeconds);
                }

                // Clean up the connection on EVERY exit path where it was created
                // (health-check failure, connect exception, OR cancellation). The
                // compare-and-swap removes only this exact instance, never an
                // already-replaced newer one.
                if (ads is not null)
                {
                    // Remove first, THEN announce Disconnected: a handler that
                    // calls GetConnection during the event must not observe the
                    // about-to-be-disposed instance.
                    var wasCurrent = _connections.TryRemove(
                        new KeyValuePair<string, IManagedConnection>(plcId, ads));

                    // Clear the facade's current pointer, but only if it still
                    // points at THIS connection. Compare-and-clear mirrors the
                    // _connections compare-and-remove above: a newer connection
                    // (e.g. from ForceReconnect) that has already replaced ours
                    // is left intact, so a stale teardown never blanks a live
                    // facade.
                    if (_facades.TryGetValue(plcId, out var facadeToClear))
                        facadeToClear.ClearCurrent(ads);

                    // Announce Disconnected only when this loop owns the current
                    // state — either it still held the live connection (wasCurrent),
                    // or it never published one (connect failed: its own
                    // Connecting -> Disconnected cycle). If a NEWER loop (e.g. from
                    // ForceReconnect) has already replaced this connection, that
                    // loop now owns the state; emitting Disconnected here would
                    // clobber its Connected.
                    if (wasCurrent || !published)
                        SetState(plcId, ConnectionState.Disconnected);

                    // Grace period: let in-flight operations on old connection
                    // finish. On the cancelled path the token is already
                    // signalled, so this falls through immediately — StopAsync
                    // must not stall here.
                    try { await Task.Delay(DisposeGracePeriod, _timeProvider, cts.Token).ConfigureAwait(false); }
                    catch { /* Clean up anyway on cancellation */ }

                    ads.ForceDisconnect();
                    ads.Dispose();
                }

                if (cancelled || cts.Token.IsCancellationRequested) break;

                // Wait before next connection attempt
                try { await Task.Delay(delay, _timeProvider, cts.Token).ConfigureAwait(false); }
                catch { break; }

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxReconnectDelay.Ticks));
            }
        }, CancellationToken.None);
    }
}
