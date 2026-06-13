using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads;

internal sealed class AdsConnectionPool : IHostedService, IAdsConnectionPool, IDisposable
{
    private readonly Dictionary<string, PlcTargetOptions> _targets;
    private readonly SymbolDumpOptions _symbolDump;
    private readonly IAdsConnectionFactory _connectionFactory;
    private readonly AdsRouterReadySignal _readySignal;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AdsConnectionPool> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, IManagedConnection> _connections = new(StringComparer.OrdinalIgnoreCase);

    // Stable per-target facades. Created EAGERLY IN THE CONSTRUCTOR — one per
    // CONFIGURED target, independent of whether (or when) that target's loop ever
    // connects. Creating them in the ctor (rather than StartAsync) makes
    // GetConnection total from the moment of construction: it can return a non-null
    // facade and never throws UnknownPlcTargetException for any configured id,
    // even before StartAsync is called. A facade's identity never changes for the
    // pool's lifetime; the loop pushes the live underlying connection into it via
    // SetCurrent and clears it via ClearCurrent at exactly the points it updates
    // _connections.
    private readonly ConcurrentDictionary<string, AdsConnectionFacade> _facades = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _reconnectCts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _loopTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConnectionState> _states = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _stoppingCts;

    // Background task that awaits the router signal and, on Ready, releases the
    // deferred real-target loops. Tracked like a loop task so StopAsync can await
    // it (it exits promptly via the stopping token). Null when there are no real
    // targets (all-sim configs never wait on the router).
    private Task? _routerWaitTask;

    // Set true once the real-target loops have been released (router became
    // Ready). Until then, ForceReconnect must NOT start a real loop — doing so
    // would bypass the router gate. Volatile: written from the router wait task,
    // read from the (possibly different) thread that calls ForceReconnect.
    private volatile bool _realLoopsReleased;

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
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider)
    {
        var value = options.Value;
        _targets = value.Targets;
        _symbolDump = value.Diagnostics.SymbolDump;
        _connectionFactory = connectionFactory;
        _readySignal = readySignal;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AdsConnectionPool>();
        _timeProvider = timeProvider;

        // Eagerly create one stable facade per CONFIGURED target in the constructor
        // so GetConnection is total from construction (before StartAsync). The
        // facade is pure state with no I/O — creating it here is side-effect-free.
        // Each facade receives its own ILogger<AdsConnectionFacade> so that facade-
        // originated warnings (state-handler exceptions, dropped typed notifications)
        // are logged under category Dahlke.TwinCAT.Ads.AdsConnectionFacade —
        // distinguishable from pool-management noise in category AdsConnectionPool.
        foreach (var (plcId, targetOptions) in _targets)
            _facades[plcId] = new AdsConnectionFacade(plcId, targetOptions, _timeProvider,
                loggerFactory.CreateLogger<AdsConnectionFacade>());
    }

    /// <summary>
    /// Starts the pool. Hosted-service start is never delayed by router
    /// availability: simulated-target loops start immediately, and
    /// real-target loops are deferred behind a tracked background wait task that
    /// releases them once the router signals Ready. <see cref="StartAsync"/>
    /// itself returns promptly in every case.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Facades are already created in the constructor; StartAsync only starts
        // the connection loops that push live connections into those facades.

        var simTargets = _targets
            .Where(kvp => kvp.Value.Mode == ConnectionMode.Simulated)
            .ToList();
        var realTargets = _targets
            .Where(kvp => kvp.Value.Mode == ConnectionMode.Real)
            .ToList();

        // Simulated loops never need the router — start them immediately, always.
        foreach (var (plcId, options) in simTargets)
            StartConnectionLoop(plcId, options);

        if (realTargets.Count == 0)
        {
            // All targets are simulated — no router wait task at all (the router
            // service already gates itself).
            _logger.LogInformation(
                "ADS connection pool starting — all {Count} target(s) are simulated, skipping router wait",
                _targets.Count);
            _realLoopsReleased = true; // no real loops to gate
            return Task.CompletedTask;
        }

        // Real targets exist: defer their loops until the router becomes ready.
        // StartAsync returns promptly; the wait happens on a tracked background
        // task observed by StopAsync (exactly like a connection loop task).
        _logger.LogInformation(
            "ADS connection pool starting — real target loops deferred until router ready: {Ids}",
            string.Join(", ", realTargets.Select(kvp => kvp.Key)));

        var stoppingToken = _stoppingCts.Token;
        _routerWaitTask = Task.Run(async () =>
        {
            try
            {
                await _readySignal.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException)
            {
                // The signal resolved to a terminal non-Ready state. With the
                // retry-forever router this only happens at shutdown (Cancelled)
                // or the defensive Failed path — never a transient bind failure.
                //   * InvalidOperationException → router FAILED. Its InnerException
                //     is the captured reason; log it so operators know WHY.
                //   * TaskCanceledException → router CANCELLED (host shutting down).
                // Real loops stay unreleased either way.
                if (ex is InvalidOperationException)
                {
                    _logger.LogWarning(
                        ex.InnerException ?? ex,
                        "ADS router failed to start: {Reason} — {RealCount} real target(s) not started: {RealIds}",
                        ex.InnerException?.Message ?? ex.Message,
                        realTargets.Count,
                        string.Join(", ", realTargets.Select(kvp => kvp.Key)));
                }
                else
                {
                    _logger.LogInformation(
                        "ADS router wait cancelled (shutting down) — {RealCount} real target(s) not started",
                        realTargets.Count);
                }

                return;
            }

            // Router is ready: release the deferred real-target loops. Start the
            // loops FIRST, then open the gate. Doing it in this order eliminates a
            // double-start race with ForceReconnect: were the flag set first, a
            // ForceReconnect arriving in the window before StartConnectionLoop runs
            // would see the gate open, find no existing loop, and start its OWN —
            // which the loop started here would then overwrite, orphaning a CTS.
            // With this order, a ForceReconnect in the (now harmless) window
            // observes the gate still closed and is refused (it warns and no-ops),
            // while the loop started here proceeds normally.
            _logger.LogInformation(
                "ADS router ready, connecting to {Count} real target(s)", realTargets.Count);

            foreach (var (plcId, options) in realTargets)
                StartConnectionLoop(plcId, options);

            _realLoopsReleased = true;
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ADS connection pool stopping...");

        // Cancel the stopping token FIRST so a still-parked router wait task
        // (awaiting the pending signal) unblocks promptly. Await it to settle
        // BEFORE snapshotting the loop set: if the router had just become Ready,
        // the wait task may be mid-flight starting real loops — letting it finish
        // ensures every real loop it spawns is registered in _loopTasks before we
        // cancel and drain them, so none is orphaned.
        _stoppingCts?.Cancel();

        if (_routerWaitTask is not null)
        {
            try { await _routerWaitTask.WaitAsync(TimeSpan.FromSeconds(10), _timeProvider, cancellationToken); }
            catch { /* Timeout or cancellation — clean up anyway */ }
        }

        // Cancel background loops (now the complete set, including any real loops
        // the router wait task released just before settling).
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
        _routerWaitTask = null;
        _stoppingCts?.Cancel();
        _stoppingCts?.Dispose();
        _stoppingCts = null;
    }

    /// <inheritdoc/>
    public IAdsConnection GetConnection(string plcId)
    {
        // Facades are created eagerly in the constructor — one per configured target.
        // For a configured id this is always non-null and never throws, regardless
        // of connection state or whether StartAsync has been called.
        if (_facades.TryGetValue(plcId, out var facade))
            return facade;

        // Unknown id: throw with the full set of configured ids so the caller can
        // immediately see whether the problem is a typo. Keys are sorted for a
        // stable, predictable message across runs.
        throw new UnknownPlcTargetException(
            plcId,
            _targets.Keys.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
    }

    /// <inheritdoc/>
    public bool TryGetConnection(string plcId, [NotNullWhen(true)] out IAdsConnection? connection)
    {
        if (_facades.TryGetValue(plcId, out var facade))
        {
            connection = facade;
            return true;
        }

        connection = null;
        return false;
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
    /// Returns a snapshot of per-target connection state for health reporting.
    /// Each tuple contains the target identifier, its configured
    /// <see cref="ConnectionMode"/>, and the current <see cref="ConnectionState"/>
    /// as observed by the connection loop at the moment of the call.
    /// </summary>
    /// <remarks>
    /// The list is ordered by target identifier (ordinal, case-insensitive) for a
    /// stable, predictable representation in dashboards and health-check responses.
    /// This is a lightweight read of in-memory state and does not block.
    /// </remarks>
    /// <returns>
    /// A read-only list of <c>(PlcId, Mode, State)</c> tuples, ordered by target
    /// identifier, representing a point-in-time snapshot of each target's state.
    /// </returns>
    internal IReadOnlyList<(string PlcId, ConnectionMode Mode, ConnectionState State)> GetTargetStates()
    {
        return _targets
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => (
                kvp.Key,
                kvp.Value.Mode,
                _states.TryGetValue(kvp.Key, out var s) ? s : ConnectionState.Disconnected))
            .ToList();
    }

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

        var args = new ConnectionStateChangedEventArgs(plcId, next, previous);

        // Forward to the facade FIRST so a pool-event handler that cross-reads
        // facade.State observes the new value, never a stale one. The facade
        // handles per-handler exception isolation internally and never
        // propagates exceptions back here.
        if (_facades.TryGetValue(plcId, out var facade))
            facade.OnStateChanged(args);

        var handlers = ConnectionStateChanged;
        if (handlers is not null)
        {
            // Invoke per-handler so one throwing subscriber does not skip the
            // rest (same isolation guarantee as the facade's public event).
            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<ConnectionStateChangedEventArgs>)handler)(this, args);
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
        }
    }

    /// <inheritdoc/>
    public bool TryGetSimulatedConnection(string plcId, [NotNullWhen(true)] out SimulatedAdsConnection? simulated)
    {
        // Reach through the stable facade to its current underlying managed
        // connection and type-test it. Returns false for real targets (the current
        // connection is an AdsConnection, not a SimulatedAdsConnection), unknown
        // ids (no facade), and unstarted/mid-startup simulated targets (the loop has
        // not yet published a connection, so CurrentForTesting is null). Never throws
        // — this is a test-support API.
        if (_facades.TryGetValue(plcId, out var facade)
            && facade.CurrentForTesting is SimulatedAdsConnection sim)
        {
            simulated = sim;
            return true;
        }

        simulated = null;
        return false;
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

        // Router gate: a real target's loop is deferred until the router
        // becomes ready. If the loops have not yet been released, starting one
        // here would bypass the gate and connect before the router exists. Refuse
        // and warn — the loop will start on its own once the router is ready.
        if (!_realLoopsReleased)
        {
            _logger.LogWarning(
                "ForceReconnect: router not ready; real target {PlcId} loop will start when it is",
                plcId);
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
