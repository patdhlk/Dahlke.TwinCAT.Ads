using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Caches one <see cref="AdsRawChannel"/> per <c>(amsNetId, port)</c> and runs the
/// idle sweeper that releases their transports.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sweeper never disposes anything.</b> It asks each channel to evict
/// itself via <see cref="AdsRawChannel.TryEvictIfIdle"/>; the channel is the sole
/// owner of its transport. Splitting that ownership is what produced the
/// three-instalment teardown race in #9/#13/#15.
/// </para>
/// <para>
/// <b>The channel dictionary is never pruned.</b> Channel identity must stay
/// stable for the factory's lifetime — callers hold references indefinitely, and
/// Task 7's subscriptions are re-registered through the channel that owns them.
/// A disconnected channel holds only its Net ID, port and clock, so the cost is
/// bounded by the number of distinct targets addressed.
/// </para>
/// </remarks>
internal sealed class AdsRawChannelFactory : IAdsRawChannelFactory, IHostedService, IDisposable
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<(string NetId, int Port), AdsRawChannel> _channels =
        new(ChannelKeyComparer.Instance);
    private readonly ConcurrentDictionary<(string NetId, int Port), SimulatedRawStore> _stores =
        new(ChannelKeyComparer.Instance);

    /// <summary>
    /// Net ID spellings already warned about — see <see cref="WarnOnceAboutLaundering"/>.
    /// Keyed on the caller's raw string, so each distinct spelling is reported once.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _warnedNetIds = new(StringComparer.Ordinal);

    private readonly AdsRawChannelOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AdsRawChannelFactory> _logger;
    private readonly TimeProvider _timeProvider;

    private CancellationTokenSource? _sweeperCts;
    private Task? _sweeperTask;
    private bool _stopped;

    public AdsRawChannelFactory(
        IOptions<TwinCatAdsOptions> options,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider)
    {
        _options = options.Value.RawChannels;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AdsRawChannelFactory>();
        _timeProvider = timeProvider;
    }

    /// <summary>Number of channels currently cached. Test-support.</summary>
    internal int ChannelCount => _channels.Count;

    public IAdsRawChannel Get(string amsNetId, int port)
    {
        // A null argument is a caller PROGRAMMING ERROR, categorically different
        // from a malformed-but-present target. Totality covers the latter — an
        // empty, whitespace or nonsense Net ID yields a channel that simply fails
        // when operated on — but it was never meant to paper over a null, which
        // would otherwise surface as a NullReferenceException from Trim().
        ArgumentNullException.ThrowIfNull(amsNetId);

        var key = (NetId: NormaliseNetId(amsNetId, out var laundered), Port: port);

        if (laundered)
            WarnOnceAboutLaundering(amsNetId, key.NetId);

        return _channels.GetOrAdd(key, k => new AdsRawChannel(
            k.NetId, k.Port, CreateTransport, _options,
            _loggerFactory.CreateLogger<AdsRawChannel>(), _timeProvider));
    }

    /// <summary>
    /// Warns exactly once for each distinct spelling whose octets were laundered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deduped on the SPELLING, not on channel creation.</b> Warning from
    /// <c>GetOrAdd</c>'s factory delegate would miss the one ordering that
    /// actually confuses people: the canonical spelling requested first and the
    /// malformed one second, where the channel already exists so the delegate
    /// never runs. That is precisely the case where a caller sees two "different"
    /// targets sharing state and cannot work out why — a diagnostic silent in its
    /// motivating case is not doing its job.
    /// </para>
    /// <para>
    /// <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> also makes this
    /// exactly-once under contention, which the delegate could not promise:
    /// <c>GetOrAdd</c> may invoke its factory more than once when several threads
    /// race to create the same channel.
    /// </para>
    /// <para>
    /// Only laundered spellings are ever inserted, so the set is bounded by the
    /// number of distinct malformed spellings a caller uses — not by traffic.
    /// </para>
    /// </remarks>
    private void WarnOnceAboutLaundering(string requested, string resolved)
    {
        if (!_warnedNetIds.TryAdd(requested, 0))
            return;

        _logger.LogWarning(
            "Raw channel Net ID '{Requested}' has an octet outside 0-255; it resolves to " +
            "'{Resolved}', which is the device this channel will actually address. The same " +
            "Net ID in a RawChannels:Seed key fails validation at startup instead.",
            requested, resolved);
    }

    public bool TryGetSimulated(
        string amsNetId, int port,
        [NotNullWhen(true)] out ISimulatedRawChannel? simulated)
    {
        ArgumentNullException.ThrowIfNull(amsNetId);

        simulated = null;

        if (_options.Mode != ConnectionMode.Simulated)
            return false;

        // The store exists independently of any transport, so seeding before the
        // first operation works and survives every later eviction.
        simulated = GetOrCreateStore(NormaliseNetId(amsNetId), port);
        return true;
    }

    /// <summary>
    /// Canonicalises a caller-supplied AMS Net ID so every spelling of one
    /// physical device lands on ONE channel and ONE store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the dictionary key is whatever string the caller typed, so
    /// <c>"1.2.3.4.5.6"</c>, <c>"01.2.3.4.5.6"</c> and <c>" 1.2.3.4.5.6"</c>
    /// become three channels with three stores addressing one target — and in
    /// simulation a seed applied under one spelling is invisible under another,
    /// which reads as "seeding silently doesn't work".
    /// </para>
    /// <para>
    /// Trim first: <c>AmsNetId.TryParse</c> canonicalises <c>"01.2.3.4.5.6"</c> to
    /// <c>"1.2.3.4.5.6"</c> but does NOT tolerate leading whitespace. An
    /// unparseable ID keys on the trimmed original rather than throwing, because
    /// <see cref="IAdsRawChannelFactory.Get"/> is documented total — it validates
    /// nothing and discovers reachability by operating.
    /// </para>
    /// <para>
    /// <b>The emptiness guard is load-bearing, not defensive.</b>
    /// <c>AmsNetId.TryParse</c> is itself NOT total: it THROWS
    /// <see cref="ArgumentException"/> on an empty string rather than returning
    /// <see langword="false"/>, and <c>Trim()</c> turns a whitespace-only argument
    /// into one. Calling it unguarded would break the totality this method exists
    /// to preserve, for the very input a discovery scan is most likely to produce.
    /// </para>
    /// <para>
    /// Note this deliberately inherits <c>AmsNetId</c>'s out-of-range laundering
    /// (<c>"999.1.1.1.1.1"</c> becomes <c>"0.1.1.1.1.1"</c>), reporting it through
    /// <paramref name="laundered"/> so the caller can warn. That is the right call
    /// HERE, and only here: the transport resolves the ID the same way at
    /// <c>Connect()</c> — <c>AmsNetId.Parse</c> launders identically to
    /// <c>TryParse</c> — so the two spellings genuinely address one device and
    /// collapsing them keeps the key agreeing with the wire. <c>RawSeedParser</c>
    /// takes the opposite line and rejects such an ID outright, because a
    /// configured seed key is an operator's stated intent, not a runtime lookup.
    /// </para>
    /// </remarks>
    /// <param name="amsNetId">The caller-supplied Net ID. Must not be null.</param>
    /// <param name="laundered">
    /// <see langword="true"/> when the ID parsed but had an octet outside 0-255, so
    /// the returned key addresses a DIFFERENT device than the text suggests.
    /// </param>
    private static string NormaliseNetId(string amsNetId, out bool laundered)
    {
        var trimmed = amsNetId.Trim();

        if (trimmed.Length > 0 && AmsNetId.TryParse(trimmed, out var parsed))
        {
            laundered = !RawSeedParser.IsWellFormedNetId(trimmed);
            return parsed.ToString();
        }

        laundered = false;
        return trimmed;
    }

    /// <inheritdoc cref="NormaliseNetId(string, out bool)"/>
    private static string NormaliseNetId(string amsNetId) => NormaliseNetId(amsNetId, out _);

    /// <summary>
    /// Creates a transport for one channel — always a FRESH instance, in both
    /// modes.
    /// </summary>
    /// <remarks>
    /// In simulated mode the freshness is preserved by keeping the durable state
    /// in a <see cref="SimulatedRawStore"/> the factory owns and handing each new
    /// connection a reference to it. Seeded fixtures and runtime writes therefore
    /// outlive an idle eviction, while
    /// <see cref="AdsRawChannel"/>'s retry (which re-creates the transport) and its
    /// <c>ReferenceEquals</c> drop guard both keep working. Returning one shared
    /// connection instead would make retry a no-op in simulation and open an ABA
    /// window in which a late drop disposes a transport another caller just
    /// installed.
    /// </remarks>
    private IManagedRawConnection CreateTransport(string amsNetId, int port)
    {
        // Refuse to mint a transport nothing will ever dispose. The raw factory is
        // registered last so it stops FIRST; a consumer hosted service stopping
        // after us can still call Get and operate, and without this that operation
        // would open a live AdsClient after the shutdown sweep had already passed.
        //
        // Fails fast rather than waiting, mirroring the pool's documented rule for
        // the same situation: a transport will never be published again, so burning
        // the timeout would only delay shutdown.
        //
        // The check is ordered by AdsRawChannel's transport gate, which both this
        // (via GetOrCreateTransportAsync) and Shutdown hold: because _stopped is set
        // BEFORE the shutdown loop, whichever side takes the gate first, the other
        // sees a consistent answer — either we install and Shutdown disposes it, or
        // Shutdown ran and we throw. Neither ordering leaks.
        if (Volatile.Read(ref _stopped))
        {
            throw new AdsConnectionUnavailableException(
                $"{amsNetId}:{port}",
                $"Raw channel {amsNetId}:{port} cannot open a transport: " +
                "the raw channel factory has shut down.",
                null);
        }

        return _options.Mode == ConnectionMode.Simulated
            ? new SimulatedRawConnection(amsNetId, port, GetOrCreateStore(amsNetId, port))
            : new BeckhoffManagedRawConnection(amsNetId, port);
    }

    /// <summary>
    /// Returns the durable simulated state for one target, creating it — pre-loaded
    /// with any matching configured seed — on first request.
    /// </summary>
    private SimulatedRawStore GetOrCreateStore(string amsNetId, int port) =>
        _stores.GetOrAdd((amsNetId, port), key => CreateStore(key.NetId, key.Port));

    private SimulatedRawStore CreateStore(string amsNetId, int port)
    {
        var store = new SimulatedRawStore();

        foreach (var (channelKey, slots) in _options.Seed)
        {
            if (!RawSeedParser.TryParseChannelKey(channelKey, out var seedNetId, out var seedPort, out _))
                continue;   // validated at startup; a survivor here is not fatal

            // Normalise the CONFIGURED id too: amsNetId arrives already normalised
            // from Get/TryGetSimulated, so a seed key spelled "01.2.3.4.5.6:851"
            // would otherwise never match the "1.2.3.4.5.6" channel it names.
            if (!string.Equals(NormaliseNetId(seedNetId), amsNetId, StringComparison.OrdinalIgnoreCase) ||
                seedPort != port)
                continue;

            foreach (var (slotKey, payload) in slots)
            {
                if (RawSeedParser.TryParseSlotKey(slotKey, out var ig, out var io, out _) &&
                    RawSeedParser.TryParseHex(payload, out var bytes, out _))
                {
                    store.Seed(ig, io, bytes);
                }
            }
        }

        return store;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _sweeperCts = new CancellationTokenSource();
        _sweeperTask = RunSweeperAsync(_sweeperCts);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The sweeper owns the token source it was handed and is the only thing that
    /// disposes it, in a <c>finally</c> after it has exited and can no longer hold
    /// a registration. Every other teardown path cancels only. This is the
    /// ownership discipline #15 established.
    /// </summary>
    private async Task RunSweeperAsync(CancellationTokenSource cts)
    {
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                await Task.Delay(SweepInterval, _timeProvider, cts.Token).ConfigureAwait(false);
                SweepOnce();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            // Retract the source from the field BEFORE disposing it, so a teardown
            // path can never find and cancel a disposed source. On the normal exit
            // RequestSweeperStop has already nulled the field and this is a no-op;
            // it earns its keep on an ABNORMAL exit — anything escaping SweepOnce
            // outside its per-channel try, realistically a throwing ILogger in the
            // warning path — where the field would otherwise still point here and
            // the next StopAsync/Dispose would throw ObjectDisposedException before
            // shutting a single channel down. CompareExchange, not Exchange: only
            // retract OUR source, never one a later StartAsync installed.
            Interlocked.CompareExchange(ref _sweeperCts, null, cts);
            cts.Dispose();
        }
    }

    /// <summary>Runs one eviction pass. Exposed so tests drive it without a timer.</summary>
    internal void SweepOnce()
    {
        var idleAfter = TimeSpan.FromMilliseconds(_options.IdleEvictionMs);

        foreach (var (_, channel) in _channels)
        {
            try
            {
                channel.TryEvictIfIdle(idleAfter);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Idle eviction of raw channel {NetId}:{Port} failed; it will be retried next sweep.",
                    channel.AmsNetId, channel.Port);
            }
        }
    }

    /// <summary>
    /// Cancels the sweeper without disposing its source, at most once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The source is taken out of the field with an <see cref="Interlocked"/>
    /// exchange so exactly ONE teardown path ever cancels it, no matter how many
    /// run or in what order. The normal host sequence is
    /// <see cref="StopAsync"/> then <see cref="Dispose"/>: by the time
    /// <see cref="Dispose"/> runs, <see cref="StopAsync"/> has awaited the sweeper
    /// and the sweeper has disposed the source in its <c>finally</c>. A second
    /// <c>Cancel()</c> on that source throws
    /// <see cref="ObjectDisposedException"/> — <c>Cancel()</c> is NOT
    /// safe after disposal — so the second path must find nothing to cancel rather
    /// than be guarded by a swallowed exception.
    /// </para>
    /// <para>
    /// This still never disposes the source: that remains the sweeper's job alone,
    /// which is the discipline #15 established.
    /// </para>
    /// </remarks>
    private void RequestSweeperStop() =>
        Interlocked.Exchange(ref _sweeperCts, null)?.Cancel();

    public async Task StopAsync(CancellationToken ct)
    {
        // Ordered BEFORE the shutdown loop, and load-bearing: see CreateTransport.
        Volatile.Write(ref _stopped, true);

        RequestSweeperStop();   // cancel only — the sweeper disposes its own source

        if (_sweeperTask is { } task)
            await task.ConfigureAwait(false);

        foreach (var (_, channel) in _channels)
            channel.Shutdown();
    }

    public void Dispose()
    {
        Volatile.Write(ref _stopped, true);   // before the loop: see CreateTransport

        RequestSweeperStop();   // cancel only, never dispose: see RunSweeperAsync

        foreach (var (_, channel) in _channels)
            channel.Shutdown();
    }

    /// <summary>Case-insensitive on the Net ID, exact on the port.</summary>
    private sealed class ChannelKeyComparer : IEqualityComparer<(string NetId, int Port)>
    {
        public static readonly ChannelKeyComparer Instance = new();

        public bool Equals((string NetId, int Port) x, (string NetId, int Port) y) =>
            x.Port == y.Port && StringComparer.OrdinalIgnoreCase.Equals(x.NetId, y.NetId);

        public int GetHashCode((string NetId, int Port) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.NetId), obj.Port);
    }
}
