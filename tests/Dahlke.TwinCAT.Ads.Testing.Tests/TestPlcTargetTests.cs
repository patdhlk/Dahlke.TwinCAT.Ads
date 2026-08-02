using Dahlke.TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Testing.Tests;

/// <summary>
/// Pins <see cref="TestPlcTarget"/> — the driver handle. The rule that carries the most
/// weight here is that a HARNESS write does not enter the write log: the log answers
/// "what did the code under test write", and without the exclusion a test that primes a
/// symbol and then asserts the SUT wrote it passes while testing nothing.
/// </summary>
public class TestPlcTargetTests
{
    [Fact]
    public async Task Seed_IsSilentAndUnrecorded()
    {
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();
        var target = plc.Target("plc1");

        var notified = 0;
        await plc.Connection("plc1").SubscribeAsync("GVL.Temp", 100, (_, _) => notified++);

        target.Seed("GVL.Temp", 21.5f);

        Assert.Equal(0, notified);
        Assert.Empty(target.Writes);
        Assert.Equal(21.5f, target.Read<float>("GVL.Temp"));
    }

    [Fact]
    public async Task Write_FiresSubscriptions_ButIsNotRecorded()
    {
        // Both halves matter. It must fire, or a SUT that subscribes never reacts to the
        // input the test is driving; it must not record, or AssertWritten sees the fixture.
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();
        var target = plc.Target("plc1");

        var notified = 0;
        await plc.Connection("plc1").SubscribeAsync("GVL.Temp", 100, (_, _) => notified++);

        target.Write("GVL.Temp", 30f);

        Assert.Equal(1, notified);
        Assert.Empty(target.Writes);
        Assert.Equal(30f, target.Read<float>("GVL.Temp"));
    }

    [Fact]
    public async Task AWriteByTheCodeUnderTest_IsRecorded()
    {
        await using var plc = await TestPlc.Create()
            .WithTarget("plc1", seed => seed["GVL.Setpoint"] = 20f)
            .StartAsync();

        await plc.Connection("plc1").WriteValueAsync("GVL.Setpoint", 23.5f);

        var write = Assert.Single(plc.Target("plc1").Writes);
        Assert.Equal("GVL.Setpoint", write.SymbolPath);
        Assert.Equal(23.5f, write.Value);
        Assert.Equal(20f, write.PreviousValue);
        Assert.True(write.Changed);
    }

    [Fact]
    public async Task AnUnchangedWriteByTheCodeUnderTest_IsStillRecorded()
    {
        // The case the value store cannot answer, and the reason the log exists.
        await using var plc = await TestPlc.Create()
            .WithTarget("plc1", seed => seed["GVL.Setpoint"] = 23.5f)
            .StartAsync();

        await plc.Connection("plc1").WriteValueAsync("GVL.Setpoint", 23.5f);

        var write = Assert.Single(plc.Target("plc1").Writes);
        Assert.False(write.Changed);
    }

    [Fact]
    public async Task WritesTo_FiltersByPath_CaseInsensitively()
    {
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();
        var conn = plc.Connection("plc1");

        await conn.WriteValueAsync("GVL.A", 1);
        await conn.WriteValueAsync("GVL.B", 2);
        await conn.WriteValueAsync("GVL.A", 3);

        var target = plc.Target("plc1");
        Assert.Equal(3, target.Writes.Count);
        Assert.Equal(2, target.WritesTo("GVL.A").Count);
        Assert.Equal(2, target.WritesTo("gvl.a").Count);
        Assert.Equal([1, 3], target.WritesTo("GVL.A").Select(w => w.Value));
    }

    [Fact]
    public async Task ClearWrites_EmptiesTheLog()
    {
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();

        await plc.Connection("plc1").WriteValueAsync("GVL.A", 1);
        plc.Target("plc1").ClearWrites();

        Assert.Empty(plc.Target("plc1").Writes);
    }

    [Fact]
    public async Task BatchWriteByTheCodeUnderTest_IsRecordedPerEntry()
    {
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();

        // A null entry is rejected per-symbol by WriteValuesAsync and never stored, so it
        // must not appear in the log either — the log records what was written, and a
        // rejected entry was not.
        await plc.Connection("plc1").WriteValuesAsync(new Dictionary<string, object?>
        {
            ["GVL.A"] = 1,
            ["GVL.B"] = 2,
            ["GVL.C"] = null,
        });

        var writes = plc.Target("plc1").Writes;
        Assert.Equal(2, writes.Count);
        Assert.Contains(writes, w => w.SymbolPath == "GVL.A" && Equals(w.Value, 1));
        Assert.Contains(writes, w => w.SymbolPath == "GVL.B" && Equals(w.Value, 2));
        Assert.Empty(plc.Target("plc1").WritesTo("GVL.C"));
    }

    [Fact]
    public async Task AReactiveWriteTriggeredByAHarnessWrite_IsRecorded()
    {
        // THE Critical regression guard. SimulatedAdsConnection fires subscription
        // callbacks synchronously, INSIDE the write that triggered them — so a SUT that
        // reacts to a driven input by writing an output does so literally inside
        // target.Write's call stack, before Write ever returns. A suppression mechanism
        // that covers "everything that happens during a harness Write" (a boolean flag
        // held for the call's duration) wrongly swallows this write too. It is exactly
        // the write this log exists to catch: without it, driving GVL.Temp and then
        // asserting the SUT wrote GVL.Heater would find nothing, passing while testing
        // nothing — the same false pass the whole package exists to prevent, mirrored
        // onto the harness's own drive call.
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();
        var conn = plc.Connection("plc1");
        var target = plc.Target("plc1");

        await conn.SubscribeAsync("GVL.Temp", 100, (_, _) =>
            conn.WriteValueAsync("GVL.Heater", true).GetAwaiter().GetResult());

        target.Write("GVL.Temp", 30f);

        Assert.True(target.Read<bool>("GVL.Heater"));
        var write = Assert.Single(target.WritesTo("GVL.Heater"));
        Assert.Equal(true, write.Value);
        Assert.Empty(target.WritesTo("GVL.Temp"));
    }

    [Fact]
    public async Task AReactiveWriteToADifferentTarget_IsRecordedOnThatTargetsLog()
    {
        // The exclusion buffer lives on TestPlcTarget as an instance field, not a static
        // one shared by the type: a write driven on plc1 must not suppress a reaction
        // the code under test makes on plc2 in response.
        await using var plc = await TestPlc.Create()
            .WithTarget("plc1")
            .WithTarget("plc2")
            .StartAsync();
        var conn1 = plc.Connection("plc1");
        var conn2 = plc.Connection("plc2");

        await conn1.SubscribeAsync("GVL.Trigger", 100, (_, _) =>
            conn2.WriteValueAsync("GVL.Mirror", 42).GetAwaiter().GetResult());

        plc.Target("plc1").Write("GVL.Trigger", true);

        var write = Assert.Single(plc.Target("plc2").WritesTo("GVL.Mirror"));
        Assert.Equal(42, write.Value);
        Assert.Empty(plc.Target("plc1").WritesTo("GVL.Trigger"));
    }

    [Fact]
    public async Task ConcurrentSutWrites_DuringAHarnessWrite_AreAllRecorded()
    {
        // The AsyncLocal boundary, forced to genuinely overlap rather than merely
        // interleave by luck. A TaskCompletionSource-based handshake (await a signal,
        // then write) does NOT prove this: the continuation is posted to the thread
        // pool and, in practice, runs only after target.Write has already returned and
        // reset its exclusion flag — so a naive per-instance bool passes too, by
        // accident of timing, not by correctness. To force the SUT's write to execute
        // literally while the harness write is still on the stack, the "SUT" task is
        // started (and so captures its own execution context) BEFORE the harness ever
        // touches the flag, and a blocking handshake pins the harness thread inside its
        // own write call for exactly as long as the SUT's writes take.
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();
        var conn = plc.Connection("plc1");
        var target = plc.Target("plc1");

        using var releaseSut = new SemaphoreSlim(0, 1);
        using var sutDone = new SemaphoreSlim(0, 1);

        // Started up front, so its execution context — and, for a correct AsyncLocal
        // implementation, the exclusion flag baked into that context — is captured now,
        // before the harness write below ever sets it.
        var sutWrites = Task.Run(async () =>
        {
            await releaseSut.WaitAsync();
            for (var i = 0; i < 20; i++)
                await conn.WriteValueAsync("GVL.Out", i);
            sutDone.Release();
        });

        // Fires synchronously inside target.Write, on the harness's own thread, while
        // its exclusion flag is set. Releasing the SUT here and blocking until it
        // finishes guarantees the SUT's 20 writes execute inside the harness write's
        // dynamic extent — genuine overlap, not a race that usually resolves one way.
        //
        // The wait result is captured here and asserted AFTER target.Write returns,
        // not inside the callback: SubscriberRegistry.SubscriberList.Fire (see
        // src/Dahlke.TwinCAT.Ads/SubscriberRegistry.cs) catches every exception a
        // callback throws and routes it to a log warning rather than letting it
        // propagate. An Assert.True inside the callback would be swallowed on failure —
        // Write would return normally, the SUT's writes would then run with no
        // suppression active at all, all 20 would be recorded, and the test would PASS
        // for the wrong reason: silently degenerating back into a non-discriminating
        // test with no guard actually enforced.
        var sutCompletedInTime = false;
        await conn.SubscribeAsync("GVL.Trigger", 100, (_, _) =>
        {
            releaseSut.Release();
            sutCompletedInTime = sutDone.Wait(TimeSpan.FromSeconds(10));
        });

        target.Write("GVL.Trigger", true);
        Assert.True(sutCompletedInTime, "SUT writes did not complete in time.");
        await sutWrites;

        Assert.Equal(20, target.WritesTo("GVL.Out").Count);
        Assert.Empty(target.WritesTo("GVL.Trigger"));
    }

    [Fact]
    public async Task Read_ReturnsTheCurrentValue_TypedAndUntyped()
    {
        await using var plc = await TestPlc.Create()
            .WithTarget("plc1", seed => seed["GVL.Temp"] = 21.5f)
            .StartAsync();

        var target = plc.Target("plc1");
        Assert.Equal(21.5f, target.Read<float>("GVL.Temp"));
        Assert.Equal(21.5f, target.Read("GVL.Temp"));
    }

    [Fact]
    public async Task SetRpc_SeedsAMethodResult()
    {
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();

        // AdsRpcResult has no FromReturnValue factory — it is a plain positional record
        // (ReturnValue, OutParameters). See src/Dahlke.TwinCAT.Ads/AdsRpcResult.cs.
        plc.Target("plc1").SetRpc("MAIN.Motor", "Start", _ => new AdsRpcResult(true, []));

        var result = await plc.Connection("plc1").InvokeRpcMethodAsync("MAIN.Motor", "Start", []);
        Assert.Equal(true, result.ReturnValue);
    }

    [Fact]
    public async Task Simulated_IsTheLiveConnectionBehindTheFacade()
    {
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();

        plc.Target("plc1").Simulated.SetInitialValues(
            new Dictionary<string, object?> { ["GVL.Temp"] = 99f });

        Assert.Equal(99f, await plc.Connection("plc1").ReadValueAsync<float>("GVL.Temp"));
    }

    [Fact]
    public async Task Target_IsStableAcrossCalls()
    {
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();

        // The log would be useless if each call handed back a fresh recorder.
        Assert.Same(plc.Target("plc1"), plc.Target("plc1"));
        Assert.Same(plc.Target("plc1"), plc.Target("PLC1"));
    }

    [Fact]
    public async Task Target_ForAnUnknownId_Throws()
    {
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();

        Assert.Throws<UnknownPlcTargetException>(() => plc.Target("nope"));
    }

    [Fact]
    public async Task PlcId_ReturnsTheConfiguredIdentifier()
    {
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();

        Assert.Equal("plc1", plc.Target("plc1").PlcId);
    }

    [Fact]
    public async Task Write_NullValue_Throws()
    {
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();
        var target = plc.Target("plc1");

        Assert.Throws<ArgumentNullException>(() => target.Write("GVL.Temp", null));
    }

    [Fact]
    public async Task SeedDictionary_IsSilentAndUnrecorded()
    {
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();
        var target = plc.Target("plc1");

        var notified = 0;
        await plc.Connection("plc1").SubscribeAsync("GVL.Temp", 100, (_, _) => notified++);

        target.Seed(new Dictionary<string, object?> { ["GVL.Temp"] = 21.5f, ["GVL.Other"] = 1 });

        Assert.Equal(0, notified);
        Assert.Empty(target.Writes);
        Assert.Equal(21.5f, target.Read<float>("GVL.Temp"));
        Assert.Equal(1, target.Read<int>("GVL.Other"));
    }

    [Fact]
    public async Task Detach_IsIdempotent()
    {
        // Detach is internal, not IDisposable: a cached target that a consumer could
        // `using` would stop recording while every assertion kept passing. Idempotency
        // still matters — TestPlc.DisposeAsync detaches every target, and the constructor
        // unwind path may already have detached some of them.
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();
        var target = plc.Target("plc1");

        target.Detach();
        target.Detach();
    }

    [Fact]
    public async Task TestPlcDisposeAsync_DetachesRecorders_SoALaterWriteIsNotRecorded()
    {
        // Not merely "the pool stopped" (TestPlcTests already covers that) — specifically
        // that the recorder's own event subscription was torn down, so a write reaching
        // the still-live SimulatedAdsConnection object after disposal is not recorded.
        var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();
        var target = plc.Target("plc1");
        var simulated = target.Simulated;

        await plc.DisposeAsync();

        await simulated.WriteValueAsync("GVL.A", 1);

        Assert.Empty(target.Writes);
    }

    [Fact]
    public async Task ReEntrantHarnessWrite_FromASubscriptionCallback_RecordsOnlyTheGenuineSutReaction()
    {
        // N1: a subscription callback that itself calls target.Write again — a natural
        // cascade ("once the SUT acks, drive the next input") — must not let the outer
        // Write's own event slip into the log, and must not lose a genuine SUT reaction
        // nested inside the inner Write either. A version that clears the AsyncLocal
        // unconditionally in Write's finally (rather than saving/restoring the previous
        // value) lets the inner call's finally de-install the OUTER scope: the outer
        // write's own event then finds no scope and gets recorded directly, while the
        // outer scope's real entries are stranded with nowhere to commit to.
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();
        var conn = plc.Connection("plc1");
        var target = plc.Target("plc1");

        // The cascade: GVL.Trigger's callback drives GVL.NextStep via a second,
        // re-entrant target.Write call — harness code, not the code under test.
        await conn.SubscribeAsync("GVL.Trigger", 100, (_, _) => target.Write("GVL.NextStep", true));

        // The genuine SUT: reacts to GVL.NextStep — the harness's re-entrant write — by
        // writing GVL.A. This is the one write that belongs in the log.
        await conn.SubscribeAsync("GVL.NextStep", 100, (_, _) =>
            conn.WriteValueAsync("GVL.A", 1).GetAwaiter().GetResult());

        target.Write("GVL.Trigger", true);

        var write = Assert.Single(target.Writes);
        Assert.Equal("GVL.A", write.SymbolPath);
        Assert.Empty(target.WritesTo("GVL.Trigger"));
        Assert.Empty(target.WritesTo("GVL.NextStep"));
    }

    [Fact]
    public async Task ATaskRunSpawnedFromASubscriptionCallback_JoinedAfterWriteReturns_IsRecorded()
    {
        // N2: AsyncLocal flows into a Task.Run spawned from inside a callback, carrying
        // whatever buffer/scope was installed along with it — even though that task
        // runs on its OWN thread, outside the synchronous chain the discard-last rule
        // depends on. If that task's write lands after Write's own finally has already
        // read and cleared the scope, a version that does not check which thread
        // installed the scope appends to an orphaned buffer nobody will ever read again
        // — silently lost, not merely misattributed.
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();
        var conn = plc.Connection("plc1");
        var target = plc.Target("plc1");

        // A timer-based delay (Task.Delay) yields a continuation the scheduler tends to
        // resume on a DIFFERENT pool thread, which passed even against the pre-fix
        // thread-only check for reasons that had nothing to do with correctness — the
        // test discriminated by scheduler luck, not by the property it claimed to pin.
        // Task.Yield's continuation is far more likely to resume on the SAME thread
        // that queued it, which is the scenario that actually matters here.
        Task? sutTask = null;
        await conn.SubscribeAsync("GVL.Trigger", 100, (_, _) =>
        {
            sutTask = Task.Run(async () =>
            {
                await Task.Yield();
                await conn.WriteValueAsync("GVL.Late", 1);
            });
        });

        target.Write("GVL.Trigger", true);
        Assert.NotNull(sutTask);
        await sutTask;

        var write = Assert.Single(target.WritesTo("GVL.Late"));
        Assert.Equal(1, write.Value);
    }

    [Fact]
    public async Task AWriteResumingOnTheInstallingThreadAfterWriteReturned_IsRecorded()
    {
        // The Critical this pins, reproduced deterministically rather than as a stress
        // loop (a flaky-red test is nearly as bad as a flaky-green one). A thread-ID
        // check alone asks "same thread?" but never "is this scope still finished?".
        // AsyncLocal keeps a scope reachable from ANY continuation whose
        // ExecutionContext was captured while that scope was installed, and nothing
        // stops the thread pool from resuming such a continuation on the very thread
        // that installed the scope — after that scope's own Write call has already
        // returned and drained it. ExecutionContext.Run reproduces exactly that:
        // capture the context live, during Write, then re-enter it synchronously, on
        // this same thread, once Write has already returned.
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();
        var conn = plc.Connection("plc1");
        var target = plc.Target("plc1");

        ExecutionContext? capturedDuringWrite = null;
        await conn.SubscribeAsync("GVL.Trigger", 100, (_, _) =>
            capturedDuringWrite = ExecutionContext.Capture());

        target.Write("GVL.Trigger", true);
        Assert.NotNull(capturedDuringWrite);

        // Runs synchronously on THIS thread — the same thread that just ran Write()
        // above — but with the ambient AsyncLocal state rewound to what it was
        // mid-Write, while the scope was still installed. Write already returned, so
        // that scope's own finally has already run: Closed is true.
        ExecutionContext.Run(
            capturedDuringWrite,
            _ => conn.WriteValueAsync("GVL.Late", 1).GetAwaiter().GetResult(),
            null);

        var write = Assert.Single(target.WritesTo("GVL.Late"));
        Assert.Equal(1, write.Value);
    }

    [Fact]
    public void RawThreadStart_FlowsAsyncLocal()
    {
        // Not a guard on its own — it establishes the mechanism the next two tests are
        // built on, so a future reader does not have to take it on faith. A raw
        // Thread.Start captures the ambient ExecutionContext, so every AsyncLocal value
        // live on the starting thread — including TestPlcTarget's own HarnessWriteScope —
        // is visible on the new thread even though that thread is a genuinely different
        // physical one. Without that flow, "a cross-thread write that can still see an
        // open scope" would not be a reachable state at all, and the two tests below
        // would be proving nothing.
        var ambient = new AsyncLocal<string?>();
        ambient.Value = "set-on-parent";

        string? seenOnChild = null;
        var childThreadId = 0;

        var child = new Thread(() =>
        {
            seenOnChild = ambient.Value;
            childThreadId = Environment.CurrentManagedThreadId;
        });
        child.Start();
        child.Join();

        Assert.Equal("set-on-parent", seenOnChild);
        Assert.NotEqual(Environment.CurrentManagedThreadId, childThreadId);
    }

    [Fact]
    public async Task ACrossThreadWriteAfterTheHarnessOwnEvent_ButBeforeWriteReturns_IsRecorded()
    {
        // Pins the ThreadId conjunct in OnValueWritten — the one qualifier there that
        // the Closed conjunct cannot stand in for. Delete
        // `scope.ThreadId == Environment.CurrentManagedThreadId` and every other test in
        // the suite stays green while this one goes red.
        //
        // The window is exact rather than lucky, from two facts in
        // SimulatedAdsConnection: RaiseValueWritten walks GetInvocationList() in
        // subscription order and TestPlcTarget subscribes in its own constructor, so
        // OnValueWritten always runs FIRST; and RaiseValueWritten is called from inside
        // WriteValueAsync, which runs inside Write's `try`, so every handler runs BEFORE
        // Write's finally. A SECOND ValueWritten handler attached by the test
        // (target.Simulated is public and documented for exactly this) therefore runs
        // post-raise and pre-finally: the harness's own GVL.Trigger write is ALREADY the
        // last element of scope.Buffered, and the scope is still open — Closed is false,
        // so the Closed conjunct alone lets a write straight through.
        //
        // A cross-thread write landing in that window, with the scope flowed in via
        // ExecutionContext (see RawThreadStart_FlowsAsyncLocal above), becomes the
        // buffer's NEW last element — and discard-last then eats it: the genuine SUT
        // write silently lost, and the harness's own GVL.Trigger committed to the log in
        // its place. Exactly the false pass this package exists to prevent. Thread.Start
        // plus Join makes it deterministic — no timing, no scheduler luck, no stress loop.
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();
        var conn = plc.Connection("plc1");
        var target = plc.Target("plc1");

        var workerThreadId = 0;

        void SecondHandler(object? sender, SimulatedWriteEventArgs e)
        {
            // Only react to the harness's own drive, never to the worker's own write —
            // otherwise this handler recurses.
            if (!string.Equals(e.SymbolPath, "GVL.Trigger", StringComparison.OrdinalIgnoreCase))
                return;

            var worker = new Thread(() =>
            {
                workerThreadId = Environment.CurrentManagedThreadId;
                conn.WriteValueAsync("GVL.Late", 1).GetAwaiter().GetResult();
            });
            worker.Start();
            worker.Join();
        }

        target.Simulated.ValueWritten += SecondHandler;
        try
        {
            target.Write("GVL.Trigger", true);
        }
        finally
        {
            target.Simulated.ValueWritten -= SecondHandler;
        }

        // Asserted so a future change that quietly runs the worker inline cannot make
        // this test pass for a reason unrelated to the property it pins.
        Assert.NotEqual(0, workerThreadId);
        Assert.NotEqual(Environment.CurrentManagedThreadId, workerThreadId);

        var write = Assert.Single(target.WritesTo("GVL.Late"));
        Assert.Equal(1, write.Value);
        Assert.Empty(target.WritesTo("GVL.Trigger"));
    }

    [Fact]
    public async Task ANestedHarnessWriteOnAnotherThread_DoesNotCommitIntoTheOuterScope()
    {
        // Pins the ThreadId conjunct on the OTHER decision point — the nested-commit
        // branch in Write's finally (`previous.ThreadId ==
        // Environment.CurrentManagedThreadId`). Delete it and this is the only test in
        // the suite that fails.
        //
        // Same deterministic post-raise / pre-finally window as the test above, opened
        // the same way with a second ValueWritten handler, for the same reasons. What
        // differs is what runs in it: a nested harness Write, on another thread. That
        // inner call flows the OUTER scope in as `previous`, and the outer scope is
        // still open — so `!previous.Closed` alone lets it through. Without the ThreadId
        // conjunct the inner call hands its remainder into the outer thread's live
        // buffer, where it becomes the new last element; the outer finally's discard-last
        // then eats the genuine SUT reaction and commits the harness's own outer write in
        // its place.
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();
        var conn = plc.Connection("plc1");
        var target = plc.Target("plc1");

        // The genuine code under test: reacts to GVL.Nested by writing GVL.SutReaction.
        // That reaction is the one write that belongs in the log.
        await conn.SubscribeAsync("GVL.Nested", 100, (_, _) =>
            conn.WriteValueAsync("GVL.SutReaction", 7).GetAwaiter().GetResult());

        void SecondHandler(object? sender, SimulatedWriteEventArgs e)
        {
            if (!string.Equals(e.SymbolPath, "GVL.Trigger", StringComparison.OrdinalIgnoreCase))
                return;

            var worker = new Thread(() => target.Write("GVL.Nested", true));
            worker.Start();
            worker.Join();
        }

        target.Simulated.ValueWritten += SecondHandler;
        try
        {
            target.Write("GVL.Trigger", true);
        }
        finally
        {
            target.Simulated.ValueWritten -= SecondHandler;
        }

        // Asserted as the whole log, not just the one path, so a failure prints what
        // actually landed there — which is how you tell "lost" from "substituted".
        Assert.Equal(["GVL.SutReaction"], target.Writes.Select(w => w.SymbolPath));
    }

    [Fact]
    public async Task ANestedHarnessWriteUnderAStaleOuterScope_DoesNotCommitIntoIt()
    {
        // Pins the fourth and last qualifier: `!previous.Closed` on the nested-commit
        // branch in Write's finally. Delete it and, again, this is the only test that
        // fails.
        //
        // Same ExecutionContext.Capture/Run technique as
        // AWriteResumingOnTheInstallingThreadAfterWriteReturned_IsRecorded, and just as
        // deterministic — re-entering a captured context is a synchronous call on THIS
        // thread, not a pool continuation the scheduler might place elsewhere. The one
        // difference is what the re-entered context does: target.Write rather than a
        // plain connection write, which moves the decision point from OnValueWritten to
        // the nested-commit branch. `previous` is then the ALREADY-CLOSED outer scope, on
        // the very thread that installed it — the ThreadId conjunct matches, and only
        // Closed stands between the genuine SUT reaction and a buffer whose own Write
        // call already drained and abandoned it.
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();
        var conn = plc.Connection("plc1");
        var target = plc.Target("plc1");

        ExecutionContext? capturedDuringWrite = null;
        await conn.SubscribeAsync("GVL.Trigger", 100, (_, _) =>
            capturedDuringWrite = ExecutionContext.Capture());

        // The genuine code under test: reacts to GVL.Nested by writing GVL.SutReaction.
        await conn.SubscribeAsync("GVL.Nested", 100, (_, _) =>
            conn.WriteValueAsync("GVL.SutReaction", 7).GetAwaiter().GetResult());

        target.Write("GVL.Trigger", true);
        Assert.NotNull(capturedDuringWrite);

        // Same thread as the Write above, but with the ambient scope rewound to the
        // now-closed outer one.
        ExecutionContext.Run(capturedDuringWrite, _ => target.Write("GVL.Nested", true), null);

        Assert.Equal(["GVL.SutReaction"], target.Writes.Select(w => w.SymbolPath));
    }
}
