using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// The stable per-<c>(amsNetId, port)</c> facade returned by
/// <see cref="IAdsRawChannelFactory.Get"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> This facade is the SOLE owner of its
/// <see cref="IManagedRawConnection"/>. Nothing else creates, disposes or
/// replaces it — the factory's idle sweeper REQUESTS eviction via
/// <see cref="TryEvictIfIdle"/> and never touches the transport itself. Ownership
/// ambiguity under concurrent teardown is what produced the three-instalment race
/// in #9/#13/#15; stating one owner here is the mitigation.
/// </para>
/// </remarks>
internal sealed class AdsRawChannel : IAdsRawChannel
{
    private readonly ManagedRawConnectionFactory _create;
    private readonly AdsRawChannelOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _transportGate = new(1, 1);

    /// <summary>
    /// The live subscriptions this channel owns — held by the shared registry
    /// module, which owns every durable-subscription invariant (publish-before-
    /// first-registration, reserve/commit-or-hand-back, restore-on-swap, the
    /// delivery guarantee). This channel is one of its two adapters;
    /// <see cref="AdsConnectionFacade"/> is the other. The registry — NOT the
    /// transport — is the single source of truth for whether a subscription
    /// exists: it is what re-registration reads after a drop, what pins the
    /// channel against eviction, and what <see cref="Deliver"/> consults before
    /// invoking a handler. Adapter-specific policy is injected in the
    /// constructor: a registration is a device notification handle scoped to the
    /// transport that issued it, discarding one is
    /// <see cref="RemoveNotification"/>, each restore attempt is bounded by the
    /// per-attempt timeout linked to THIS CHANNEL'S shutdown (never a caller's
    /// token), and a restore pass stops early once shutdown is requested.
    /// </summary>
    private readonly DurableSubscriptionRegistry<IManagedRawConnection, uint, RawSubscriptionInfo> _subscriptions;

    /// <summary>
    /// Raised at most once, at <see cref="Shutdown"/> (which runs from BOTH the
    /// factory's <c>StopAsync</c> and <c>Dispose</c>), so work holding the
    /// transport gate can abandon what is about to be thrown away. A signal with
    /// no owning loop, deliberately never retired — the teardown discipline, and
    /// why an unretired signal is safe, live in
    /// <see cref="OwnedLoopCancellation"/>.
    /// </summary>
    private readonly OwnedLoopCancellation _shutdown = new();

    private IManagedRawConnection? _transport;
    private long _lastUseTicks;
    private int _inFlight;

    public AdsRawChannel(
        string amsNetId,
        int port,
        ManagedRawConnectionFactory create,
        AdsRawChannelOptions options,
        ILogger logger,
        TimeProvider timeProvider)
    {
        AmsNetId = amsNetId;
        Port = port;
        _create = create;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
        _lastUseTicks = timeProvider.GetUtcNow().UtcTicks;
        _subscriptions = new DurableSubscriptionRegistry<IManagedRawConnection, uint, RawSubscriptionInfo>(
            discard: RemoveNotification,
            restoreBound: () =>
            {
                var timeout = new CancellationTokenSource(DefaultTimeout, _timeProvider);
                var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, timeout.Token);
                return new SubscriptionRestoreBound(linked.Token, linked, timeout);
            },
            stopRestoring: () => _shutdown.IsStopRequested,
            onRestoreFailure: (info, ex) => _logger.LogWarning(ex,
                "Could not restore raw subscription {NetId}:{Port} IG=0x{IG:X} IO={IO} after a transport drop.",
                AmsNetId, Port, info.IndexGroup, info.IndexOffset));
    }

    public string AmsNetId { get; }
    public int Port { get; }

    public ConnectionState State =>
        Volatile.Read(ref _transport) is { IsConnected: true }
            ? ConnectionState.Connected
            : ConnectionState.Disconnected;

    /// <summary>When this channel last completed an operation. Read by the sweeper.</summary>
    internal DateTimeOffset LastUseUtc => new(Volatile.Read(ref _lastUseTicks), TimeSpan.Zero);

    /// <summary>
    /// Live subscription count. A channel with any live subscription is pinned
    /// against idle eviction — the sweeper will not release a transport that is
    /// serving notifications.
    /// </summary>
    internal int LiveSubscriptionCount => _subscriptions.Count;

    public Task<int> ReadAsync(uint ig, uint io, Memory<byte> destination, CancellationToken ct) =>
        ReadAsync(ig, io, destination, DefaultTimeout, ct);

    public Task<int> ReadAsync(uint ig, uint io, Memory<byte> destination, TimeSpan timeout, CancellationToken ct) =>
        ExecuteAsync(ig, io, timeout, ct, (t, c) => t.ReadAsync(ig, io, destination, c));

    public Task WriteAsync(uint ig, uint io, ReadOnlyMemory<byte> source, CancellationToken ct) =>
        WriteAsync(ig, io, source, DefaultTimeout, ct);

    public async Task WriteAsync(uint ig, uint io, ReadOnlyMemory<byte> source, TimeSpan timeout, CancellationToken ct) =>
        await ExecuteAsync(ig, io, timeout, ct, async (t, c) =>
        {
            await t.WriteAsync(ig, io, source, c).ConfigureAwait(false);
            return 0;
        }).ConfigureAwait(false);

    public Task<int> ReadWriteAsync(
        uint ig, uint io, Memory<byte> destination, ReadOnlyMemory<byte> source, CancellationToken ct) =>
        ReadWriteAsync(ig, io, destination, source, DefaultTimeout, ct);

    public Task<int> ReadWriteAsync(
        uint ig, uint io, Memory<byte> destination, ReadOnlyMemory<byte> source,
        TimeSpan timeout, CancellationToken ct) =>
        ExecuteAsync(ig, io, timeout, ct, (t, c) => t.ReadWriteAsync(ig, io, destination, source, c));

    public async Task<StateInfo> ReadStateAsync(CancellationToken ct)
    {
        StateInfo state = default;
        await ExecuteAsync(0, 0, DefaultTimeout, ct, async (t, c) =>
        {
            state = await t.ReadStateAsync(c).ConfigureAwait(false);
            return 0;
        }).ConfigureAwait(false);
        return state;
    }

    /// <inheritdoc />
    public Task<IDisposable> SubscribeAsync(
        uint indexGroup, uint indexOffset, int length,
        int cycleTimeMs, RawNotificationHandler handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var info = new RawSubscriptionInfo(indexGroup, indexOffset, length, cycleTimeMs, handler);

        // AddAsync publishes the record BEFORE the initial registration below
        // runs — a transport rebuild in the gap then SEES this subscription and
        // restores it — and rolls the record back if that registration fails, so
        // a never-registered subscription cannot linger pinning the channel
        // against eviction. The reservation/commit mechanics live in the registry;
        // the registrar closes the delivery callback over its own record so
        // Deliver can consult registry membership as the liveness check. The
        // transport is recorded ALONGSIDE the handle by the registry because a
        // handle is transport-scoped: number 5 on the transport that issued it is
        // not number 5 on its replacement.
        return _subscriptions.AddAsync(
            info,
            (record, transport, token) => transport.AddNotificationAsync(
                info.IndexGroup, info.IndexOffset, info.Length, info.CycleTimeMs,
                data => Deliver(record, data), token),
            initialRegister: async record =>
            {
                // ONE bound over the whole registration, built the way
                // RunAttemptsAsync builds its own. Without it the only thing
                // standing between a caller and a dead target is AdsClient.Timeout
                // — an invisible third-party default this library never sets,
                // which neither tracks AdsRawChannelOptions.TimeoutMs nor surfaces
                // as TimeoutException. It covers the transport build too, because
                // a subscribe that is queued behind a slow rebuild is just as
                // stuck as one waiting on the device.
                using var timeoutCts = new CancellationTokenSource(DefaultTimeout, _timeProvider);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                try
                {
                    var transport = await GetOrCreateTransportAsync(linkedCts.Token).ConfigureAwait(false);
                    await _subscriptions.RegisterAsync(record, transport, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Caller cancellation, never dressed up as a timeout.
                    throw new OperationCanceledException(ct);
                }
                catch (OperationCanceledException)
                {
                    // NOT retried, deliberately: a retry means dropping and
                    // rebuilding the transport, which recurses straight back into
                    // restoring every OTHER subscription on this channel.
                    throw CancellationDisambiguator.CreateRawException(
                        ct, AmsNetId, Port, indexGroup, indexOffset, _options.TimeoutMs);
                }
            });
    }

    /// <summary>
    /// Invokes a subscriber's handler. A throwing handler is logged and swallowed:
    /// one bad subscriber must not tear down its own subscription, nor stop the
    /// transport delivering to others.
    /// </summary>
    /// <remarks>
    /// The registry lookup is the liveness check, not an optimisation. Removal
    /// from the transport is a round trip that handle disposal does not wait for
    /// and that can fail outright; the registry entry, by contrast, is gone
    /// before <see cref="IDisposable.Dispose"/> returns. Consulting it here is what
    /// makes "no handler fires after disposal completes" true rather than hopeful.
    /// </remarks>
    private void Deliver(
        DurableSubscriptionRegistry<IManagedRawConnection, uint, RawSubscriptionInfo>.Record record,
        ReadOnlyMemory<byte> data)
    {
        if (!_subscriptions.Contains(record))
            return;

        var info = record.Metadata;
        try
        {
            info.Handler(data.Span);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Raw notification handler for {NetId}:{Port} IG=0x{IG:X} IO={IO} threw; subscription retained.",
                AmsNetId, Port, info.IndexGroup, info.IndexOffset);
        }
    }

    /// <summary>
    /// Removes one device notification, fire-and-forget, absorbing every failure.
    /// </summary>
    /// <remarks>
    /// Nothing here is allowed to throw: this runs from
    /// <see cref="IDisposable.Dispose"/>, which is documented idempotent and safe,
    /// and the interesting case is precisely the one that fails — the transport has
    /// already been dropped after a timeout or released at shutdown. Both shapes of
    /// failure have to be absorbed: a disposed
    /// <see cref="SimulatedRawConnection"/> throws SYNCHRONOUSLY, because its
    /// removal is not an <c>async</c> method and the exception therefore escapes
    /// the call instead of landing in the returned task. Not awaiting is safe
    /// because the registry entry is already gone, so <see cref="Deliver"/> drops
    /// anything the transport sends from here on even if this never succeeds.
    /// </remarks>
    private void RemoveNotification(IManagedRawConnection transport, uint handle)
    {
        try
        {
            _ = transport.RemoveNotificationAsync(handle, CancellationToken.None)
                .ContinueWith(
                    t => LogRemovalFailed(t.Exception, handle),
                    TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            LogRemovalFailed(ex, handle);
        }
    }

    private void LogRemovalFailed(Exception? ex, uint handle) =>
        _logger.LogDebug(ex,
            "Removing raw notification {Handle} on {NetId}:{Port} failed; the registry entry is already gone.",
            handle, AmsNetId, Port);

    private TimeSpan DefaultTimeout => TimeSpan.FromMilliseconds(_options.TimeoutMs);

    /// <summary>
    /// Runs one operation with the per-attempt timeout and the retry policy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A timeout with no device answer disposes the transport and re-creates it
    /// before reissuing. That re-creation is the whole point: consumers currently
    /// build a fresh client per retry precisely because it clears the stall, and
    /// reusing the stalled transport would be a regression.
    /// </para>
    /// <para>
    /// The call is registered as in flight for its WHOLE duration, retries
    /// included. <see cref="LastUseUtc"/> alone is not enough to protect it: that
    /// is stamped on completion, so a call that runs longer than the idle window
    /// would look idle while it was still running and the sweeper would dispose
    /// the transport underneath it.
    /// </para>
    /// </remarks>
    private async Task<int> ExecuteAsync(
        uint ig, uint io, TimeSpan timeout, CancellationToken ct,
        Func<IManagedRawConnection, CancellationToken, Task<int>> operation)
    {
        var attempts = _options.RetryCount + 1;

        // Stamp on ENTRY as well as completion, and register the call, before any
        // transport is obtained. Interlocked gives the full fence the eviction
        // handshake in TryEvictIfIdle relies on.
        Touch();
        Interlocked.Increment(ref _inFlight);
        try
        {
            return await RunAttemptsAsync(ig, io, timeout, ct, operation, attempts).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task<int> RunAttemptsAsync(
        uint ig, uint io, TimeSpan timeout, CancellationToken ct,
        Func<IManagedRawConnection, CancellationToken, Task<int>> operation,
        int attempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            var transport = await GetOrCreateTransportAsync(ct).ConfigureAwait(false);

            // NOTE: CancelAfter(TimeSpan, TimeProvider) does NOT exist — only
            // CancelAfter(TimeSpan) and CancelAfter(int). The TimeProvider-aware
            // path is the CONSTRUCTOR, so the timeout source is built with the
            // clock and then linked to the caller's token.
            using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                var read = await operation(transport, linkedCts.Token).ConfigureAwait(false);
                Touch();
                return read;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancellation is never retried and never multiplied.
                throw new OperationCanceledException(ct);
            }
            catch (OperationCanceledException)
            {
                Touch();
                await DropTransportAsync(transport).ConfigureAwait(false);

                if (attempt >= attempts)
                {
                    throw CancellationDisambiguator.CreateRawException(
                        ct, AmsNetId, Port, ig, io, (int)timeout.TotalMilliseconds);
                }

                _logger.LogDebug(
                    "Raw {NetId}:{Port} IG=0x{IG:X} IO={IO} timed out (attempt {Attempt}/{Attempts}); retrying on a fresh transport.",
                    AmsNetId, Port, ig, io, attempt, attempts);
            }
            catch (AdsErrorException)
            {
                // A device answer, not transport death. Never retried, never tears down.
                Touch();
                throw;
            }
        }
    }

    private async Task<IManagedRawConnection> GetOrCreateTransportAsync(CancellationToken ct)
    {
        // Ordered AFTER the caller's Interlocked.Increment of _inFlight — that is
        // the half of the eviction handshake this read depends on.
        var existing = Volatile.Read(ref _transport);
        if (existing is not null)
            return existing;

        await _transportGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_transport is not null)
                return _transport;

            var created = _create(AmsNetId, Port);
            try
            {
                created.Connect();
            }
            catch (Exception ex)
            {
                created.Dispose();
                throw new AdsConnectionUnavailableException(
                    $"{AmsNetId}:{Port}",
                    $"Raw channel {AmsNetId}:{Port} could not be opened.",
                    ex);
            }


            // Restore BEFORE publishing, so no operation can observe a
            // half-restored channel; the transport gate held here is what keeps
            // that atomic. Per-record isolation, retain-on-failure, and the
            // no-caller-token rule (one caller walking away must never unregister
            // every subscription on the channel) live in the registry's
            // RestoreAllAsync; this channel's restore bound and shutdown
            // early-stop are configured on the registry in the constructor.
            if (!_subscriptions.IsEmpty)
                await _subscriptions.RestoreAllAsync(created).ConfigureAwait(false);

            Volatile.Write(ref _transport, created);
            return created;
        }
        finally
        {
            _transportGate.Release();
        }
    }

    /// <summary>Disposes the transport if it is still the current one.</summary>
    private async Task DropTransportAsync(IManagedRawConnection stale)
    {
        await _transportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_transport, stale))
                return;

            Volatile.Write(ref _transport, null);
            stale.Dispose();
        }
        finally
        {
            _transportGate.Release();
        }
    }

    private void Touch() => Volatile.Write(ref _lastUseTicks, _timeProvider.GetUtcNow().UtcTicks);

    /// <summary>
    /// Disposes the transport when the channel has been unused for
    /// <paramref name="idleAfter"/>, has no live subscriptions, and has no
    /// operation in flight. Called BY the sweeper; the sweeper never disposes
    /// anything itself.
    /// </summary>
    /// <returns><see langword="true"/> when a transport was actually evicted.</returns>
    internal bool TryEvictIfIdle(TimeSpan idleAfter)
    {
        if (LiveSubscriptionCount > 0)
            return false;

        if (Volatile.Read(ref _inFlight) > 0)
            return false;

        if (_timeProvider.GetUtcNow() - LastUseUtc < idleAfter)
            return false;

        if (!_transportGate.Wait(0))
            return false;   // busy connecting or dropping; try again next sweep

        try
        {
            // Claim the transport with a full fence, then re-check the in-flight
            // count. An operation registers itself with a full fence BEFORE it
            // reads _transport, so of the two orderings at least one side sees the
            // other: either the call finds the claim (null) and opens a fresh
            // transport, or this sees the call and hands the transport back
            // untouched. Without the re-check, a call that started between the
            // check above and here would be reading through a disposed transport.
            var stale = Interlocked.Exchange(ref _transport, null);
            if (stale is null)
                return false;

            // The subscription count is re-checked on the SAME handshake as the
            // in-flight count and for the same reason. A subscribe publishes itself
            // to the registry (a full fence) before it reads _transport, so of the
            // two orderings at least one side sees the other: either the subscribe
            // finds the claim and builds a fresh transport, or this sees the
            // subscription and hands the transport back. The check at the top of
            // this method alone would leave a subscribe that arrived just after it
            // registering against a transport disposed underneath it.
            if (Volatile.Read(ref _inFlight) > 0 || LiveSubscriptionCount > 0)
            {
                Volatile.Write(ref _transport, stale);
                return false;
            }

            stale.Dispose();
            _logger.LogDebug("Raw channel {NetId}:{Port} evicted after idle timeout.", AmsNetId, Port);
            return true;
        }
        finally
        {
            _transportGate.Release();
        }
    }

    /// <summary>Disposes the transport unconditionally, at factory shutdown.</summary>
    /// <remarks>
    /// <para>
    /// The shutdown signal is raised BEFORE the gate is taken, so a restore pass
    /// holding the gate can abandon its remaining subscriptions instead of making
    /// the host wait out one per-attempt timeout for each of them.
    /// </para>
    /// <para>
    /// This SHORTENS the wait; it does not skip it. The gate is still taken, so a
    /// transport is never disposed with a registration still in flight against it.
    /// Bounding the wait and proceeding anyway would put two owners on one
    /// transport's teardown, which is the shape that produced #9/#13/#15 — and this
    /// type's own remarks name single ownership as the mitigation.
    /// </para>
    /// </remarks>
    internal void Shutdown()
    {
        _shutdown.RequestStop();

        _transportGate.Wait();
        try
        {
            _transport?.Dispose();
            Volatile.Write(ref _transport, null);
        }
        finally
        {
            _transportGate.Release();
        }
    }

    /// <summary>
    /// One raw subscription's description: what to subscribe to and whom to hand
    /// the bytes to. Pure data — where the subscription is CURRENTLY registered
    /// (the transport and the handle THAT transport issued) is the registry
    /// record's business, rewritten on every re-registration while this
    /// description stays stable for the caller's lifetime.
    /// </summary>
    internal sealed record RawSubscriptionInfo(
        uint IndexGroup, uint IndexOffset, int Length, int CycleTimeMs, RawNotificationHandler Handler);
}
