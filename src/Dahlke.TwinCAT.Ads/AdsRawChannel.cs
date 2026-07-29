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
    /// The live subscriptions this channel owns, keyed by an id that stays stable
    /// for the caller's lifetime. This registry — NOT the transport — is the single
    /// source of truth for whether a subscription exists: it is what re-registration
    /// reads after a drop, what pins the channel against eviction, and what
    /// <see cref="Deliver"/> consults before invoking a handler.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, RawSubscription> _subscriptions = new();

    /// <summary>
    /// Set once, at <see cref="Shutdown"/>, so work holding the transport gate can
    /// abandon what is about to be thrown away.
    /// </summary>
    /// <remarks>
    /// Never disposed, by the same rule <c>AdsRawChannelFactory.RequestSweeperStop</c>
    /// follows: <see cref="Shutdown"/> runs from BOTH <c>StopAsync</c> and
    /// <c>Dispose</c>, and <see cref="CancellationTokenSource.Cancel()"/> is not safe
    /// after disposal. A source nobody disposes cannot be cancelled after disposal.
    /// It holds no timer and no registration of its own; the linked sources built
    /// from it are disposed per iteration, which unregisters them here.
    /// </remarks>
    private readonly CancellationTokenSource _shutdown = new();
    private int _shutdownRequested;

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
    }

    public string AmsNetId { get; }
    public int Port { get; }

    public ConnectionState State =>
        Volatile.Read(ref _transport) is { IsConnected: true }
            ? ConnectionState.Connected
            : ConnectionState.Disconnected;

    /// <summary>When this channel last completed an operation. Read by the sweeper.</summary>
    internal DateTimeOffset LastUseUtc => new(Volatile.Read(ref _lastUseTicks), TimeSpan.Zero);

    /// <summary>Total transports created — proves retry re-creates rather than reuses.</summary>
    internal int ConnectAttempts { get; private set; }

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
    public async Task<IDisposable> SubscribeAsync(
        uint indexGroup, uint indexOffset, int length,
        int cycleTimeMs, RawNotificationHandler handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var id = Guid.NewGuid();
        var subscription = new RawSubscription(indexGroup, indexOffset, length, cycleTimeMs, handler);

        // Publish to the registry BEFORE the first registration, exactly as
        // AdsConnectionFacade.SubscribeCoreAsync does for symbols: a transport
        // rebuild in the gap then SEES this subscription and restores it, where
        // registering first and publishing second would lose it. RegisterAsync's
        // reservation is what keeps that safe: it makes registration exactly-once
        // per transport when the restore gets there first, which it always does
        // when this very subscribe is what caused the transport to be built.
        _subscriptions[id] = subscription;

        try
        {
            // ONE bound over the whole registration, built the way RunAttemptsAsync
            // builds its own. Without it the only thing standing between a caller
            // and a dead target is AdsClient.Timeout — an invisible third-party
            // default this library never sets, which neither tracks
            // AdsRawChannelOptions.TimeoutMs nor surfaces as TimeoutException. It
            // covers the transport build too, because a subscribe that is queued
            // behind a slow rebuild is just as stuck as one waiting on the device.
            using var timeoutCts = new CancellationTokenSource(DefaultTimeout, _timeProvider);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                var transport = await GetOrCreateTransportAsync(linkedCts.Token).ConfigureAwait(false);
                await RegisterAsync(id, subscription, transport, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancellation, never dressed up as a timeout.
                throw new OperationCanceledException(ct);
            }
            catch (OperationCanceledException)
            {
                // NOT retried, deliberately: a retry means dropping and rebuilding
                // the transport, which recurses straight back into restoring every
                // OTHER subscription on this channel.
                throw CancellationDisambiguator.CreateRawException(
                    ct, AmsNetId, Port, indexGroup, indexOffset, _options.TimeoutMs);
            }
        }
        catch
        {
            // Roll back, or a never-registered subscription lingers in the registry
            // pinning the channel against eviction forever. Unsubscribe rather than
            // a bare remove: a restore may have got a handle onto a transport before
            // whatever failed here, and that handle has to go too.
            Unsubscribe(id);
            throw;
        }

        return new SubscriptionHandle(this, id);
    }

    /// <summary>
    /// Registers one subscription against a transport and records the resulting
    /// registration — but only if that registration still belongs to anyone by the
    /// time the device answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transport is recorded ALONGSIDE the handle because a handle is
    /// transport-scoped: number 5 on the transport that issued it is not number 5
    /// on its replacement. Removing by handle alone against whatever transport
    /// happens to be current would cancel a different subscriber's notification.
    /// </para>
    /// <para>
    /// <b>Reserve, then commit-or-hand-back</b>, the shape
    /// <see cref="AdsConnectionFacade"/>'s durable symbol subscriptions use. The
    /// reservation taken before the await makes registration exactly-once per
    /// transport — a subscribe whose own transport build restored it finds the
    /// reservation and does not register a second time. The re-check after the
    /// await is the other half: a device round trip is long enough for the
    /// subscriber to dispose its handle, or for a newer rebuild to reserve a newer
    /// transport, and a handle committed to a record nobody holds any more is one
    /// nothing will ever remove. The device would keep pushing it for the
    /// transport's whole life, and ADS notification handles are a limited
    /// per-connection resource.
    /// </para>
    /// </remarks>
    private async Task RegisterAsync(
        Guid id, RawSubscription subscription, IManagedRawConnection transport, CancellationToken ct)
    {
        if (!subscription.TryReserve(transport))
            return;   // already registered against this very transport

        uint handle;
        try
        {
            handle = await transport.AddNotificationAsync(
                subscription.IndexGroup, subscription.IndexOffset,
                subscription.Length, subscription.CycleTimeMs,
                data => Deliver(id, data), ct).ConfigureAwait(false);
        }
        catch
        {
            // Release the reservation so the next attempt — the next rebuild, or
            // the subscribe still parked on this very call — tries again.
            subscription.ReleaseReservation(transport);
            throw;
        }

        if (_subscriptions.ContainsKey(id) && subscription.TryCommit(transport, handle))
            return;

        // Nobody owns this registration: the handle was disposed while the device
        // was answering, or a newer transport took the reservation. Hand it back.
        // Ordering makes this airtight — disposal always clears the reservation, so
        // a dispose that lands after the ContainsKey check still fails the commit.
        RemoveNotification(transport, handle);
    }

    /// <summary>
    /// Invokes a subscriber's handler. A throwing handler is logged and swallowed:
    /// one bad subscriber must not tear down its own subscription, nor stop the
    /// transport delivering to others.
    /// </summary>
    /// <remarks>
    /// The registry lookup is the liveness check, not an optimisation. Removal from
    /// the transport is a round trip that <see cref="Unsubscribe"/> does not wait
    /// for and that can fail outright; the registry entry, by contrast, is gone
    /// before <see cref="IDisposable.Dispose"/> returns. Consulting it here is what
    /// makes "no handler fires after disposal completes" true rather than hopeful.
    /// </remarks>
    private void Deliver(Guid id, ReadOnlyMemory<byte> data)
    {
        if (!_subscriptions.TryGetValue(id, out var subscription))
            return;

        try
        {
            subscription.Handler(data.Span);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Raw notification handler for {NetId}:{Port} IG=0x{IG:X} IO={IO} threw; subscription retained.",
                AmsNetId, Port, subscription.IndexGroup, subscription.IndexOffset);
        }
    }

    /// <summary>
    /// Re-registers every live subscription against a freshly built transport.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called from <see cref="GetOrCreateTransportAsync"/> while the transport gate
    /// is held, so a concurrent operation cannot observe a half-restored channel.
    /// A subscription disposed during the drop is already out of the dictionary and
    /// is therefore not restored — which is what makes "exactly once" hold.
    /// </para>
    /// <para>
    /// <b>The token of the call that happened to trigger the rebuild must never
    /// reach here.</b> Rebuilds are triggered by whichever operation next touches
    /// the channel, carrying whatever token that caller passed — an ASP.NET
    /// request's <c>RequestAborted</c>, say. Threading it in would mean one caller
    /// walking away unregisters EVERY subscription on the channel: each
    /// registration throws, each is swallowed here, and the transport is published
    /// with none of them. That is permanent, not transient — the live subscriptions
    /// pin the channel against idle eviction, so on a subscription-only channel
    /// nothing ever rebuilds the transport again. Each restore is instead bounded
    /// by the configured per-attempt timeout and nothing else.
    /// </para>
    /// </remarks>
    private async Task RestoreSubscriptionsAsync(IManagedRawConnection transport)
    {
        foreach (var (id, subscription) in _subscriptions)
        {
            // Shutting down: stop restoring onto a transport that is about to be
            // disposed. Without this, a host stopping while N subscriptions cannot
            // reach a dead target waits out N full per-attempt timeouts, because
            // Shutdown blocks on the gate this loop holds.
            if (_shutdown.IsCancellationRequested)
                break;

            using var timeoutCts = new CancellationTokenSource(DefaultTimeout, _timeProvider);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                _shutdown.Token, timeoutCts.Token);
            try
            {
                await RegisterAsync(id, subscription, transport, linkedCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Retained, not removed: the next rebuild retries it, and a
                // subscribe still parked on this very call registers it itself.
                _logger.LogWarning(ex,
                    "Could not restore raw subscription {NetId}:{Port} IG=0x{IG:X} IO={IO} after a transport drop.",
                    AmsNetId, Port, subscription.IndexGroup, subscription.IndexOffset);
            }
        }
    }

    /// <summary>Removes a subscription permanently. Idempotent and thread-safe.</summary>
    private void Unsubscribe(Guid id)
    {
        if (!_subscriptions.TryRemove(id, out var subscription))
            return;

        // Taking the registration also clears the reservation, which is what lets a
        // restore still awaiting the device detect that its handle now belongs to
        // nobody and hand it straight back.
        if (subscription.TryTakeRegistration(out var transport, out var handle))
            RemoveNotification(transport, handle);
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

            ConnectAttempts++;

            // Restore BEFORE publishing, so no operation can observe a
            // half-restored channel. Subscriptions disposed during the drop are
            // already out of the dictionary and are correctly not restored.
            if (!_subscriptions.IsEmpty)
                await RestoreSubscriptionsAsync(created).ConfigureAwait(false);

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
        RequestShutdown();

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
    /// Raises the shutdown signal, at most once. Never disposes the source — see
    /// <see cref="_shutdown"/>.
    /// </summary>
    private void RequestShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) == 0)
            _shutdown.Cancel();
    }

    /// <summary>
    /// One durable subscription: what to subscribe to, whom to hand the bytes to,
    /// and where it is CURRENTLY registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The registration — the transport it lives on and the handle THAT transport
    /// issued — is rewritten on every re-registration; the <see cref="Guid"/> the
    /// record is filed under is what stays stable for the caller's lifetime.
    /// </para>
    /// <para>
    /// The two halves are kept behind a gate rather than as independent fields
    /// because they are only meaningful together: a handle is scoped to the
    /// transport that issued it, so a handle read against a different transport
    /// than the one it came from names some other subscriber's notification.
    /// </para>
    /// </remarks>
    private sealed record RawSubscription(
        uint IndexGroup, uint IndexOffset, int Length, int CycleTimeMs, RawNotificationHandler Handler)
    {
        private readonly object _gate = new();

        // The transport this subscription is reserved against, and — once the
        // device has answered — the handle that transport issued. _hasHandle
        // distinguishes "reserved, answer still pending" from "registered": only
        // the latter has anything to remove.
        private IManagedRawConnection? _transport;
        private uint _handle;
        private bool _hasHandle;

        /// <summary>
        /// Claims <paramref name="transport"/> for this subscription, unless it is
        /// already claimed for that very transport.
        /// </summary>
        /// <returns>
        /// <see langword="false"/> when this subscription is already reserved
        /// against or registered on <paramref name="transport"/>, and the caller
        /// must therefore NOT register it again.
        /// </returns>
        /// <remarks>
        /// The exactly-once guard. A subscribe that causes its own transport to be
        /// built is restored by that build, under the gate, before
        /// <see cref="GetOrCreateTransportAsync"/> returns to it; without this check
        /// it would then register a second time and every notification would be
        /// delivered twice. Any previous registration reference is dropped rather
        /// than removed: a new transport is only ever built after the previous one
        /// was disposed, which takes its notifications with it.
        /// </remarks>
        public bool TryReserve(IManagedRawConnection transport)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_transport, transport))
                    return false;

                _transport = transport;
                _handle = 0;
                _hasHandle = false;
                return true;
            }
        }

        /// <summary>
        /// Records the handle <paramref name="transport"/> has just issued, if this
        /// subscription still holds the reservation for it.
        /// </summary>
        /// <returns>
        /// <see langword="false"/> when the reservation is gone — disposed, or
        /// superseded by a newer transport — and the caller must hand the handle
        /// back to the transport instead.
        /// </returns>
        public bool TryCommit(IManagedRawConnection transport, uint handle)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_transport, transport))
                    return false;

                _handle = handle;
                _hasHandle = true;
                return true;
            }
        }

        /// <summary>
        /// Gives up the reservation on <paramref name="transport"/> after a failed
        /// registration, so the next attempt retries — but only if it is still ours.
        /// </summary>
        public void ReleaseReservation(IManagedRawConnection transport)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_transport, transport))
                    return;

                _transport = null;
                _hasHandle = false;
            }
        }

        /// <summary>
        /// Takes the current registration and clears it — reservation included — so
        /// exactly one caller ever removes it.
        /// </summary>
        /// <returns>
        /// <see langword="false"/> when there is nothing to remove: never
        /// registered, or reserved with the device's answer still outstanding. In
        /// the second case clearing the reservation is what tells the pending
        /// registration to hand its handle back.
        /// </returns>
        public bool TryTakeRegistration(
            [NotNullWhen(true)] out IManagedRawConnection? transport, out uint handle)
        {
            lock (_gate)
            {
                var registered = _hasHandle;
                transport = registered ? _transport : null;
                handle = _handle;

                _transport = null;
                _handle = 0;
                _hasHandle = false;
                return registered && transport is not null;
            }
        }
    }

    /// <summary>
    /// The caller's handle on one subscription. Holds the id, never the record:
    /// the registry is what decides liveness, and a handle that could reach past it
    /// would be a second answer to that question.
    /// </summary>
    private sealed class SubscriptionHandle : IDisposable
    {
        private readonly AdsRawChannel _channel;
        private readonly Guid _id;
        private int _disposed;

        public SubscriptionHandle(AdsRawChannel channel, Guid id)
        {
            _channel = channel;
            _id = id;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _channel.Unsubscribe(_id);
        }
    }
}
