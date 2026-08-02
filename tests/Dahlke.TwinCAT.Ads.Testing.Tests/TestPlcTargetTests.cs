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
    public async Task Dispose_IsIdempotent()
    {
        await using var plc = await TestPlc.Create().WithTarget("plc1").StartAsync();
        var target = plc.Target("plc1");

        target.Dispose();
        target.Dispose();
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
}
