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

        await plc.Connection("plc1").WriteValuesAsync(new Dictionary<string, object?>
        {
            ["GVL.A"] = 1,
            ["GVL.B"] = 2,
        });

        Assert.Equal(2, plc.Target("plc1").Writes.Count);
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
        await conn.SubscribeAsync("GVL.Trigger", 100, (_, _) =>
        {
            releaseSut.Release();
            Assert.True(sutDone.Wait(TimeSpan.FromSeconds(10)), "SUT writes did not complete in time.");
        });

        target.Write("GVL.Trigger", true);
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
}
