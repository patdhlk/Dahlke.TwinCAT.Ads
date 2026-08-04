using Dahlke.EtherCAT.Diagnostics;
using Dahlke.TwinCAT.Ads;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TwinCAT.Ads;

namespace Dahlke.EtherCAT.Diagnostics.Tests;

/// <summary>
/// Exercises <see cref="EtherCatClient.GetMastersAsync"/>'s master-discovery caching against a
/// hand-rolled <see cref="IAdsRawChannelFactory"/>/<see cref="IAdsRawChannel"/> double built with
/// NSubstitute. This is deliberately NOT <see cref="SimulatedRawChannelFixture"/>: the behaviour
/// under test is call counts and control flow (how many times each candidate Net ID is probed,
/// across repeated calls), not byte decoding, so a fake that counts <c>ReadStateAsync</c> calls
/// per Net ID is the right instrument.
///
/// The PLC's own Net ID is <see cref="PlcNetId"/>, whose byte 5 is 1, so
/// <c>DeriveMasterCandidates</c> produces exactly five distinct candidates: <see cref="PlcNetId"/>
/// itself, then byte-5 variants 2-5 (<see cref="Variant2"/>..<see cref="Variant5"/>).
/// </summary>
public class EtherCatClientMasterDiscoveryTests
{
    private const int AmsPortEcMaster = 0xFFFF;

    private const string PlcNetId = "192.168.1.136.1.1";
    private const string Variant2 = "192.168.1.136.2.1";
    private const string Variant3 = "192.168.1.136.3.1";
    private const string Variant4 = "192.168.1.136.4.1";
    private const string Variant5 = "192.168.1.136.5.1";

    private static readonly string[] AllCandidates = [PlcNetId, Variant2, Variant3, Variant4, Variant5];

    private readonly IAdsRawChannelFactory _factory = Substitute.For<IAdsRawChannelFactory>();
    private readonly Dictionary<string, IAdsRawChannel> _channels = [];
    private readonly Dictionary<string, int> _probeCounts = [];
    private readonly HashSet<string> _answering = [];
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly EtherCatClient _sut;

    public EtherCatClientMasterDiscoveryTests()
    {
        _factory.Get(Arg.Any<string>(), AmsPortEcMaster)
            .Returns(call => GetOrCreateChannel((string)call[0]));

        // FakeTimeProvider never advances on its own, so every test but the periodic-resweep one
        // below runs its whole sequence of calls at a single fixed instant — well within
        // EtherCatClient.FullSweepInterval — leaving their steady-state/invalidation assertions
        // unaffected by the freshness window this fixes in.
        _sut = new EtherCatClient(NullLogger<EtherCatClient>.Instance, _factory, _timeProvider);
    }

    private IAdsRawChannel GetOrCreateChannel(string netId)
    {
        if (_channels.TryGetValue(netId, out var existing))
            return existing;

        var channel = Substitute.For<IAdsRawChannel>();
        channel.ReadStateAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _probeCounts[netId] = _probeCounts.GetValueOrDefault(netId) + 1;

            // "An answer of any kind confirms the master" (EtherCatClient's own comment) — a bare
            // AdsState.Invalid is a legitimate "found" reply, matching real port-0xFFFF behaviour.
            return _answering.Contains(netId)
                ? Task.FromResult(new StateInfo(AdsState.Invalid, (short)0))
                : Task.FromException<StateInfo>(
                    new AdsErrorException("no answer", AdsErrorCode.TargetPortNotFound));
        });

        _channels[netId] = channel;
        return channel;
    }

    private int ProbeCount(string netId) => _probeCounts.GetValueOrDefault(netId);

    [Fact]
    public async Task GetMastersAsync_probes_only_the_cached_master_once_the_bus_is_known()
    {
        // First call: no cache yet, so every one of the five derived candidates must be probed.
        _answering.Add(Variant3);

        var first = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);

        first.Should().ContainSingle();
        first[0].AmsNetId.Should().Be(Variant3);
        first[0].DeviceId.Should().Be(0);
        foreach (var candidate in AllCandidates)
            ProbeCount(candidate).Should().Be(1, because: $"the first call has no cache and must sweep {candidate}");

        // Second call on an unchanged bus: steady state must probe ONLY the cached master. The
        // four candidates that never answer must NOT be probed again — that is the whole point
        // of this change (invariant 1: one probe per master in steady state, not five).
        var second = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);

        second.Should().ContainSingle();
        second[0].AmsNetId.Should().Be(Variant3);
        ProbeCount(Variant3).Should().Be(2, because: "steady state re-verifies the cached master");
        ProbeCount(PlcNetId).Should().Be(1, because: "steady state must not re-sweep a candidate that never answered");
        ProbeCount(Variant2).Should().Be(1, because: "steady state must not re-sweep a candidate that never answered");
        ProbeCount(Variant4).Should().Be(1, because: "steady state must not re-sweep a candidate that never answered");
        ProbeCount(Variant5).Should().Be(1, because: "steady state must not re-sweep a candidate that never answered");
    }

    [Fact]
    public async Task GetMastersAsync_re_sweeps_and_finds_a_master_that_moved_to_a_different_candidate()
    {
        // Discover the master at Variant3 first.
        _answering.Add(Variant3);
        var first = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);
        first.Single().AmsNetId.Should().Be(Variant3);

        // The bus is reconfigured: the master goes silent at Variant3 and reappears at Variant4 —
        // e.g. a TwinCAT project change moved the EtherCAT device number.
        _answering.Remove(Variant3);
        _answering.Add(Variant4);

        var second = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);

        second.Should().ContainSingle();
        second[0].AmsNetId.Should().Be(Variant4);
        second[0].DeviceId.Should().Be(0);

        // Steady state on the new master: a third call must probe only Variant4 now.
        var beforeThird = ProbeCount(Variant4);
        var third = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);

        third.Single().AmsNetId.Should().Be(Variant4);
        ProbeCount(Variant4).Should().Be(beforeThird + 1);

        // Variant3 was probed once in the first call's sweep, then twice more in the second
        // call: once to verify the (now stale) cache, and once again as part of that call's
        // full re-sweep, since it is still one of the five derived candidates. That one-time
        // double-probe on the invalidation call itself is harmless — it is not repeated on the
        // steady-state third call, which is what invariant 1 actually costs out.
        ProbeCount(Variant3).Should().Be(3);
    }

    [Fact]
    public async Task GetMastersAsync_never_caches_the_assumed_master_fallback()
    {
        // Total outage: nothing answers at all, so GetMastersAsync must fall back to the
        // synthetic "assumed master 0" at the PLC's own Net ID.
        var outage = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);

        outage.Should().ContainSingle();
        outage[0].AmsNetId.Should().Be(PlcNetId);
        outage[0].Name.Should().Contain("assumed");

        var probesAfterOutage = AllCandidates.ToDictionary(c => c, ProbeCount);

        // The outage ends: the PLC's own Net ID now genuinely answers (this happens to be the
        // fallback's own Net ID, which is exactly the scenario invariant 3 guards against — if the
        // fallback had been wrongly cached as a verified master, this call would take the
        // steady-state shortcut and probe ONLY PlcNetId, never re-checking the other four
        // candidates for a DIFFERENT or ADDITIONAL real master).
        _answering.Add(PlcNetId);

        var recovered = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);

        recovered.Should().ContainSingle();
        recovered[0].AmsNetId.Should().Be(PlcNetId);
        recovered[0].Name.Should().NotContain("assumed");

        // The discriminating assertion: every candidate got probed again on this call, proving a
        // full sweep ran rather than a steady-state check of a wrongly-cached fallback.
        foreach (var candidate in AllCandidates)
        {
            ProbeCount(candidate).Should().Be(
                probesAfterOutage[candidate] + 1,
                because: $"a genuinely un-cached recovery call must sweep {candidate}, not skip it");
        }

        // Now that PlcNetId has been genuinely verified and cached, a further call must take the
        // steady-state shortcut (invariant 1) — confirming the cache was populated correctly this
        // time, from the real discovery, not from the earlier fallback.
        var probesAfterRecovery = AllCandidates.ToDictionary(c => c, ProbeCount);
        var steadyState = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);

        steadyState.Single().AmsNetId.Should().Be(PlcNetId);
        ProbeCount(PlcNetId).Should().Be(probesAfterRecovery[PlcNetId] + 1);
        foreach (var candidate in AllCandidates.Where(c => c != PlcNetId))
        {
            ProbeCount(candidate).Should().Be(
                probesAfterRecovery[candidate],
                because: $"once the cache holds a genuinely verified master, {candidate} must not be probed again");
        }
    }

    [Fact]
    public async Task GetMastersAsync_caches_and_verifies_every_master_on_a_multi_master_rack()
    {
        // Two real masters answer.
        _answering.Add(Variant2);
        _answering.Add(Variant4);

        var first = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);

        first.Should().HaveCount(2);
        first.Select(m => m.AmsNetId).Should().BeEquivalentTo([Variant2, Variant4]);

        // Steady state must verify BOTH cached masters, and only those two.
        var second = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);

        second.Should().HaveCount(2);
        second.Select(m => m.AmsNetId).Should().BeEquivalentTo([Variant2, Variant4]);
        ProbeCount(Variant2).Should().Be(2);
        ProbeCount(Variant4).Should().Be(2);
        ProbeCount(PlcNetId).Should().Be(1);
        ProbeCount(Variant3).Should().Be(1);
        ProbeCount(Variant5).Should().Be(1);

        // One of the two masters goes silent: the whole cache must be discarded and a full sweep
        // re-run, so the survivor is still reported correctly (invariant 7).
        _answering.Remove(Variant2);

        var third = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);

        third.Should().ContainSingle();
        third[0].AmsNetId.Should().Be(Variant4);
    }

    [Fact]
    public async Task GetMastersAsync_propagates_caller_cancellation_with_a_populated_cache()
    {
        // Pins invariant 4 in the presence of a cache: an already-cancelled token must still
        // surface as OperationCanceledException, whether the candidate sweep is full or cached.
        _answering.Add(Variant3);
        await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Make even the cached candidate observe cancellation, mirroring
        // SimulatedRawConnection.ReadStateAsync's ct.ThrowIfCancellationRequested() behaviour.
        _channels[Variant3].ReadStateAsync(Arg.Any<CancellationToken>())
            .Returns(call => Task.FromException<StateInfo>(
                new OperationCanceledException((CancellationToken)call[0])));

        var act = async () => await _sut.GetMastersAsync(PlcNetId, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetMastersAsync_never_probes_a_candidate_outside_the_cache_within_the_freshness_window()
    {
        // Discover and cache Variant3. This first call is a full sweep (no cache yet), so it
        // probes Variant4 too — unanswered at this point — which is why the "unchanged" check
        // below compares against the count AFTER this call, not a hardcoded 0.
        _answering.Add(Variant3);
        var first = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);
        first.Single().AmsNetId.Should().Be(Variant3);
        var probesAfterFirst = ProbeCount(Variant4);

        // A second real master joins the bus at Variant4. Nothing cached went silent, so pure
        // failure-driven invalidation has no reason to ever look at Variant4 again.
        _answering.Add(Variant4);

        var second = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);

        second.Should().ContainSingle(
            because: "still inside the freshness window, steady state only re-verifies the cached master");
        second[0].AmsNetId.Should().Be(Variant3);
        ProbeCount(Variant4).Should().Be(
            probesAfterFirst,
            because: "steady-state verification only ever re-probes Net IDs already in the cache");
    }

    [Fact]
    public async Task GetMastersAsync_forces_a_full_sweep_once_the_freshness_window_elapses_and_finds_a_newly_joined_master()
    {
        // Discover and cache Variant3, exactly as above.
        _answering.Add(Variant3);
        var first = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);
        first.Single().AmsNetId.Should().Be(Variant3);

        // A second real master joins at Variant4 while still inside the freshness window: a call
        // right now would still only see Variant3 (pinned by the test above). Advance the clock
        // PAST EtherCatClient.FullSweepInterval — the only thing that forces GetMastersAsync to
        // look at a candidate outside the cache again.
        _answering.Add(Variant4);
        _timeProvider.Advance(EtherCatClient.FullSweepInterval + TimeSpan.FromSeconds(1));

        var second = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);

        second.Should().HaveCount(2, because: "the elapsed freshness window forces a full sweep, which is the only thing that ever looks outside the cache");
        second.Select(m => m.AmsNetId).Should().BeEquivalentTo([Variant3, Variant4]);
        ProbeCount(PlcNetId).Should().BeGreaterThan(
            0, because: "a full sweep probes every derived candidate, including the PLC's own Net ID, not just the previously cached one");

        // Steady state resumes: a third call, still within the new freshness window, must probe
        // only the two now-cached masters.
        var probesBeforeThird = AllCandidates.ToDictionary(c => c, ProbeCount);
        var third = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);

        third.Should().HaveCount(2);
        ProbeCount(Variant3).Should().Be(probesBeforeThird[Variant3] + 1);
        ProbeCount(Variant4).Should().Be(probesBeforeThird[Variant4] + 1);
        ProbeCount(PlcNetId).Should().Be(probesBeforeThird[PlcNetId]);
        ProbeCount(Variant2).Should().Be(probesBeforeThird[Variant2]);
        ProbeCount(Variant5).Should().Be(probesBeforeThird[Variant5]);
    }

    [Fact]
    public async Task GetMastersAsync_caches_independently_per_PLC()
    {
        // A second PLC target, wholly distinct from PlcNetId's candidate set.
        const string OtherPlcNetId = "192.168.2.50.1.1";
        const string OtherMaster = "192.168.2.50.3.1";

        _answering.Add(Variant3);     // PlcNetId's real master
        _answering.Add(OtherMaster);  // OtherPlcNetId's real master

        var plc1First = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);
        var plc2First = await _sut.GetMastersAsync(OtherPlcNetId, CancellationToken.None);

        plc1First.Single().AmsNetId.Should().Be(Variant3);
        plc2First.Single().AmsNetId.Should().Be(OtherMaster);

        // Steady state, per PLC: each call must probe only ITS OWN cached master. A cache keyed
        // globally instead of per PLC (a single field rather than a dictionary keyed by the PLC's
        // own Net ID) would, on this second round of calls, "verify" PLC2's request using
        // PlcNetId's cached candidate set (or vice versa, depending on write order) — probing the
        // WRONG PLC's candidates, and misreporting PLC2's master, is the discriminator here, not
        // merely getting the right answer by coincidence.
        var plc1Second = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);
        var plc2Second = await _sut.GetMastersAsync(OtherPlcNetId, CancellationToken.None);

        plc1Second.Single().AmsNetId.Should().Be(Variant3);
        plc2Second.Single().AmsNetId.Should().Be(OtherMaster);
        ProbeCount(Variant3).Should().Be(2);
        ProbeCount(OtherMaster).Should().Be(2);
        ProbeCount(PlcNetId).Should().Be(1, because: "PLC1's own candidate list must not be re-swept while verifying PLC2's cache");
        ProbeCount(OtherPlcNetId).Should().Be(1, because: "PLC2's own candidate list must not be re-swept while verifying PLC1's cache");
    }

    [Fact]
    public async Task GetMastersAsync_does_not_invalidate_the_cache_when_the_calling_token_is_already_cancelled()
    {
        // Discover and cache Variant3.
        _answering.Add(Variant3);
        var first = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);
        first.Single().AmsNetId.Should().Be(Variant3);

        // Variant3 goes quiet AND the caller's own token is already cancelled. The cached
        // verification fails with an ordinary "candidate didn't answer" (AdsErrorException, from
        // the fake's normal not-in-_answering path), not OperationCanceledException — exactly the
        // ambiguous case the ct.IsCancellationRequested guard exists for: is Variant3 genuinely
        // gone, or is this call only failing because the caller is unwinding?
        _answering.Remove(Variant3);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Nothing answers during this call (the fake never throws OperationCanceledException on
        // its own — only the dedicated cancellation tests reconfigure it to), so it completes
        // normally, falling back to the assumed-master placeholder. This call's own return value
        // is not what's under test — what happens to the CACHE is.
        await _sut.GetMastersAsync(PlcNetId, cts.Token);

        var probesAfterCancelledCall = AllCandidates.ToDictionary(c => c, ProbeCount);

        // The bus recovers and a later, healthy call comes in. If the entry survived the
        // cancelled call untouched, this call re-verifies Variant3 directly (steady state: one
        // probe). If the cancelled call above had wrongly discarded the cache, this call would
        // pay a needless full sweep instead of a single verification probe.
        _answering.Add(Variant3);
        var recovered = await _sut.GetMastersAsync(PlcNetId, CancellationToken.None);

        recovered.Should().ContainSingle();
        recovered[0].AmsNetId.Should().Be(Variant3);
        ProbeCount(Variant3).Should().Be(probesAfterCancelledCall[Variant3] + 1);
        foreach (var candidate in AllCandidates.Where(c => c != Variant3))
        {
            ProbeCount(candidate).Should().Be(
                probesAfterCancelledCall[candidate],
                because: $"the cache must have survived the cancelled call untouched, so {candidate} is not part of a full sweep here");
        }
    }
}
