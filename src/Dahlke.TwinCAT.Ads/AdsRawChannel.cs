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

    private IManagedRawConnection? _transport;
    private long _lastUseTicks;

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
    /// Live subscription count. Always zero until Task 7 adds the registry; a
    /// channel with any live subscription is pinned against idle eviction.
    /// </summary>
    internal int LiveSubscriptionCount => 0;

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

    private TimeSpan DefaultTimeout => TimeSpan.FromMilliseconds(_options.TimeoutMs);

    /// <summary>
    /// Runs one operation with the per-attempt timeout and the retry policy.
    /// </summary>
    /// <remarks>
    /// A timeout with no device answer disposes the transport and re-creates it
    /// before reissuing. That re-creation is the whole point: consumers currently
    /// build a fresh client per retry precisely because it clears the stall, and
    /// reusing the stalled transport would be a regression.
    /// </remarks>
    private async Task<int> ExecuteAsync(
        uint ig, uint io, TimeSpan timeout, CancellationToken ct,
        Func<IManagedRawConnection, CancellationToken, Task<int>> operation)
    {
        var attempts = _options.RetryCount + 1;

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
    /// <paramref name="idleAfter"/> and has no live subscriptions. Called BY the
    /// sweeper; the sweeper never disposes anything itself.
    /// </summary>
    /// <returns><see langword="true"/> when a transport was actually evicted.</returns>
    internal bool TryEvictIfIdle(TimeSpan idleAfter)
    {
        if (LiveSubscriptionCount > 0)
            return false;

        if (_timeProvider.GetUtcNow() - LastUseUtc < idleAfter)
            return false;

        if (!_transportGate.Wait(0))
            return false;   // busy connecting or dropping; try again next sweep

        try
        {
            if (_transport is null)
                return false;

            _transport.Dispose();
            Volatile.Write(ref _transport, null);
            _logger.LogDebug("Raw channel {NetId}:{Port} evicted after idle timeout.", AmsNetId, Port);
            return true;
        }
        finally
        {
            _transportGate.Release();
        }
    }

    /// <summary>Disposes the transport unconditionally, at factory shutdown.</summary>
    internal void Shutdown()
    {
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
}
