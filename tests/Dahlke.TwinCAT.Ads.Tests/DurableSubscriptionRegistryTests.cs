namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Unit tests for <see cref="DurableSubscriptionRegistry{TTarget, THandle, TMeta}"/> —
/// the one module owning the durable-subscription invariants both the facade and the
/// raw channel adapt: publish-before-first-registration, reserve → register →
/// commit-or-hand-back (exactly-once per target), restore-on-swap with per-record
/// isolation and retain-on-failure, and never a caller token in a restore.
///
/// Driven with plain fakes (a reference-identity Target, int handles, string
/// metadata) so every invariant is asserted against the registry itself rather
/// than through either adapter.
/// </summary>
public class DurableSubscriptionRegistryTests
{
    private sealed class Target { }

    private sealed class Harness
    {
        public readonly List<(Target Target, int Handle)> Discarded = new();
        public readonly List<(string Meta, Exception Error)> RestoreFailures = new();
        public Func<Target, bool>? CommitGuard;
        public Func<SubscriptionRestoreBound>? RestoreBound;
        public Func<bool>? StopRestoring;

        public DurableSubscriptionRegistry<Target, int, string> Build()
            => new(
                discard: (t, h) => Discarded.Add((t, h)),
                commitGuard: CommitGuard is null ? null : t => CommitGuard(t),
                restoreBound: RestoreBound,
                stopRestoring: StopRestoring,
                onRestoreFailure: (meta, ex) => RestoreFailures.Add((meta, ex)));
    }

    private static Task NoInitialRegister(
        DurableSubscriptionRegistry<Target, int, string>.Record record) => Task.CompletedTask;

    // =====================================================================
    // Publish-before-first-registration + exactly-once.
    // =====================================================================

    [Fact]
    public async Task AddAsync_PublishesBeforeInitialRegistration_SoARestoreDedupesIt()
    {
        // The subscribe-races-rebuild case: the record must be visible to a
        // restore BEFORE its own initial registration runs, and the reservation
        // must make registration exactly-once when the restore gets there first.
        var h = new Harness();
        var registry = h.Build();
        var target = new Target();
        var registrarCalls = 0;

        var handle = await registry.AddAsync(
            "meta",
            (_, _, _) => Task.FromResult(++registrarCalls),
            initialRegister: async record =>
            {
                // A transport rebuild restores every registered record while this
                // subscribe is still in flight...
                await registry.RestoreAllAsync(target);
                // ...then the subscribe performs its own registration, which the
                // reservation must skip.
                await registry.RegisterAsync(record, target, CancellationToken.None);
            });

        Assert.Equal(1, registrarCalls);
        Assert.Empty(h.Discarded);
        handle.Dispose();
    }

    [Fact]
    public async Task RegisterAsync_SameTargetTwice_RegistersOnce()
    {
        var h = new Harness();
        var registry = h.Build();
        var target = new Target();
        var registrarCalls = 0;

        DurableSubscriptionRegistry<Target, int, string>.Record? captured = null;
        using var handle = await registry.AddAsync(
            "meta",
            (_, _, _) => Task.FromResult(++registrarCalls),
            record => { captured = record; return Task.CompletedTask; });

        await registry.RegisterAsync(captured!, target, CancellationToken.None);
        await registry.RegisterAsync(captured!, target, CancellationToken.None);

        Assert.Equal(1, registrarCalls);
    }

    // =====================================================================
    // Failed registration releases the reservation.
    // =====================================================================

    [Fact]
    public async Task RegisterAsync_FailedRegistration_ReleasesReservation_SoTheNextAttemptRetries()
    {
        var h = new Harness();
        var registry = h.Build();
        var target = new Target();
        var calls = 0;

        DurableSubscriptionRegistry<Target, int, string>.Record? captured = null;
        using var handle = await registry.AddAsync(
            "meta",
            (_, _, _) => ++calls == 1
                ? Task.FromException<int>(new InvalidOperationException("device gone"))
                : Task.FromResult(calls),
            record => { captured = record; return Task.CompletedTask; });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.RegisterAsync(captured!, target, CancellationToken.None));

        // Same target again: the released reservation must allow the retry.
        await registry.RegisterAsync(captured!, target, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    // =====================================================================
    // Commit-or-hand-back.
    // =====================================================================

    [Fact]
    public async Task RegisterAsync_DisposedMidFlight_HandsTheFreshRegistrationBack()
    {
        var h = new Harness();
        var registry = h.Build();
        var target = new Target();
        var parked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        DurableSubscriptionRegistry<Target, int, string>.Record? captured = null;
        var handle = await registry.AddAsync(
            "meta",
            (_, _, _) => parked.Task,
            record => { captured = record; return Task.CompletedTask; });

        var inFlight = registry.RegisterAsync(captured!, target, CancellationToken.None);
        handle.Dispose();          // subscriber walks away while the device is answering
        parked.SetResult(42);      // the device answers a subscription nobody owns
        await inFlight;

        Assert.Equal([(target, 42)], h.Discarded);
    }

    [Fact]
    public async Task RegisterAsync_NewerTargetWon_HandsTheOldRegistrationBack()
    {
        var h = new Harness();
        var registry = h.Build();
        var oldTarget = new Target();
        var newTarget = new Target();
        var parked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        DurableSubscriptionRegistry<Target, int, string>.Record? captured = null;
        using var handle = await registry.AddAsync(
            "meta",
            (_, t, _) => ReferenceEquals(t, oldTarget) ? parked.Task : Task.FromResult(7),
            record => { captured = record; return Task.CompletedTask; });

        var oldInFlight = registry.RegisterAsync(captured!, oldTarget, CancellationToken.None);
        await registry.RegisterAsync(captured!, newTarget, CancellationToken.None); // newer swap wins
        parked.SetResult(3); // the old target answers late
        await oldInFlight;

        // The late answer is handed back; the newer registration is the live one.
        Assert.Equal([(oldTarget, 3)], h.Discarded);
    }

    [Fact]
    public async Task RegisterAsync_CommitGuardRefuses_HandsTheRegistrationBack()
    {
        // The facade's third commit condition: the target must still be current.
        var h = new Harness { CommitGuard = _ => false };
        var registry = h.Build();
        var target = new Target();

        DurableSubscriptionRegistry<Target, int, string>.Record? captured = null;
        using var handle = await registry.AddAsync(
            "meta",
            (_, _, _) => Task.FromResult(9),
            record => { captured = record; return Task.CompletedTask; });

        await registry.RegisterAsync(captured!, target, CancellationToken.None);

        Assert.Equal([(target, 9)], h.Discarded);
    }

    // =====================================================================
    // Dispose: exactly-once discard, idempotent, delivery liveness.
    // =====================================================================

    [Fact]
    public async Task Handle_Dispose_DiscardsTheCommittedRegistrationExactlyOnce()
    {
        var h = new Harness();
        var registry = h.Build();
        var target = new Target();

        var handle = await registry.AddAsync(
            "meta",
            (_, _, _) => Task.FromResult(11),
            record => registry.RegisterAsync(record, target, CancellationToken.None));

        handle.Dispose();
        handle.Dispose(); // idempotent

        Assert.Equal([(target, 11)], h.Discarded);
        Assert.True(registry.IsEmpty);
    }

    [Fact]
    public async Task Contains_IsTheDeliveryLivenessCheck_FalseAfterDispose()
    {
        var h = new Harness();
        var registry = h.Build();
        var target = new Target();

        DurableSubscriptionRegistry<Target, int, string>.Record? captured = null;
        var handle = await registry.AddAsync(
            "meta",
            (_, _, _) => Task.FromResult(1),
            record =>
            {
                captured = record;
                return registry.RegisterAsync(record, target, CancellationToken.None);
            });

        Assert.True(registry.Contains(captured!));
        handle.Dispose();
        Assert.False(registry.Contains(captured!));
    }

    [Fact]
    public async Task AddAsync_InitialRegistrationThrows_RollsBackAndHandsBackAnythingAcquired()
    {
        var h = new Harness();
        var registry = h.Build();
        var target = new Target();

        // The initial register acquires a registration and THEN fails (the raw
        // channel's "a restore got a handle before whatever failed here" case).
        await Assert.ThrowsAsync<TimeoutException>(() => registry.AddAsync(
            "meta",
            (_, _, _) => Task.FromResult(21),
            initialRegister: async record =>
            {
                await registry.RegisterAsync(record, target, CancellationToken.None);
                throw new TimeoutException("transport build timed out");
            }));

        Assert.True(registry.IsEmpty);
        Assert.Equal([(target, 21)], h.Discarded);
    }

    // =====================================================================
    // Restore-on-swap.
    // =====================================================================

    [Fact]
    public async Task RestoreAll_FailureIsIsolated_RecordRetained_LoopContinues()
    {
        var h = new Harness();
        var registry = h.Build();
        var first = new Target();

        var failNext = true;
        using var h1 = await registry.AddAsync(
            "flaky",
            (_, _, _) => failNext
                ? Task.FromException<int>(new InvalidOperationException("boom"))
                : Task.FromResult(1),
            NoInitialRegister);
        var laterCalls = 0;
        using var h2 = await registry.AddAsync(
            "steady",
            (_, _, _) => Task.FromResult(++laterCalls),
            NoInitialRegister);

        await registry.RestoreAllAsync(first);

        // The flaky record's failure was reported, the steady one still restored.
        Assert.Equal("flaky", Assert.Single(h.RestoreFailures).Meta);
        Assert.Equal(1, laterCalls);

        // Retained, not removed: the next swap retries the flaky record.
        failNext = false;
        await registry.RestoreAllAsync(new Target());
        Assert.Single(h.RestoreFailures); // no new failure
    }

    [Fact]
    public async Task RestoreAll_StopPredicate_BreaksEarly()
    {
        var stop = false;
        var h = new Harness { StopRestoring = () => stop };
        var registry = h.Build();
        var calls = 0;

        // Registry enumeration order is unspecified, so BOTH records flip the
        // stop signal: whichever restores first stops the pass, and the other
        // must never start.
        using var h1 = await registry.AddAsync(
            "a", (_, _, _) => { stop = true; return Task.FromResult(++calls); }, NoInitialRegister);
        using var h2 = await registry.AddAsync(
            "b", (_, _, _) => { stop = true; return Task.FromResult(++calls); }, NoInitialRegister);

        await registry.RestoreAllAsync(new Target());

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RestoreAll_BoundsEachRecordWithTheConfiguredToken_NeverACallerToken()
    {
        // RestoreAllAsync takes no CancellationToken by design — the bound comes
        // from the adapter-configured factory, one source per record.
        var h = new Harness();
        var boundsIssued = new List<CancellationTokenSource>();
        h.RestoreBound = () =>
        {
            var cts = new CancellationTokenSource();
            cts.Cancel(); // an already-cancelled bound: the registration must observe it
            boundsIssued.Add(cts);
            return new SubscriptionRestoreBound(cts.Token, cts);
        };
        var registry = h.Build();

        CancellationToken observed = default;
        using var handle = await registry.AddAsync(
            "meta",
            (_, _, ct) => { observed = ct; return Task.FromException<int>(new OperationCanceledException(ct)); },
            NoInitialRegister);

        await registry.RestoreAllAsync(new Target());

        Assert.True(observed.IsCancellationRequested);
        Assert.Single(boundsIssued);
        Assert.Single(h.RestoreFailures); // cancelled restore is retained like any failure
    }
}
