using Microsoft.Extensions.Logging.Abstractions;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Pins <see cref="SimulatedAdsConnection.ValueWritten"/>. The event exists so a test
/// harness can answer "did the code under test write this", which the value store alone
/// cannot: a value written and then overwritten, or written over an identical seeded
/// value, leaves no trace in the store.
/// </summary>
public class SimulatedWriteEventTests
{
    private static SimulatedAdsConnection Create() =>
        new("sim", "Simulated", NullLoggerFactory.Instance);

    [Fact]
    public async Task TypedWrite_RaisesValueWritten()
    {
        var sim = Create();
        var seen = new List<SimulatedWriteEventArgs>();
        sim.ValueWritten += (_, e) => seen.Add(e);

        await sim.WriteValueAsync("GVL.Temp", 21.5f);

        var e = Assert.Single(seen);
        Assert.Equal("GVL.Temp", e.SymbolPath);
        Assert.Equal(21.5f, e.Value);
        Assert.Null(e.PreviousValue);
        Assert.True(e.Changed);
    }

    [Fact]
    public async Task UntypedWrite_RaisesValueWritten_WithPreviousValue()
    {
        var sim = Create();
        await sim.WriteValueAsync("GVL.Temp", (object)21.5f);

        var seen = new List<SimulatedWriteEventArgs>();
        sim.ValueWritten += (_, e) => seen.Add(e);

        await sim.WriteValueAsync("GVL.Temp", (object)30f);

        var e = Assert.Single(seen);
        Assert.Equal(30f, e.Value);
        Assert.Equal(21.5f, e.PreviousValue);
        Assert.True(e.Changed);
    }

    [Fact]
    public async Task UnchangedWrite_StillRaises_WithChangedFalse()
    {
        // The whole reason the event is not gated on change: asserting "the SUT wrote
        // 23.5" must not depend on what the fixture happened to seed.
        var sim = Create();
        await sim.WriteValueAsync("GVL.Temp", (object)21.5f);

        var seen = new List<SimulatedWriteEventArgs>();
        sim.ValueWritten += (_, e) => seen.Add(e);

        await sim.WriteValueAsync("GVL.Temp", (object)21.5f);

        var e = Assert.Single(seen);
        Assert.False(e.Changed);
        Assert.Equal(21.5f, e.Value);
        Assert.Equal(21.5f, e.PreviousValue);
    }

    [Fact]
    public async Task BatchWrite_RaisesOncePerEntry()
    {
        var sim = Create();
        var seen = new List<SimulatedWriteEventArgs>();
        sim.ValueWritten += (_, e) => seen.Add(e);

        await sim.WriteValuesAsync(new Dictionary<string, object?>
        {
            ["GVL.A"] = 1,
            ["GVL.B"] = 2,
        });

        Assert.Equal(2, seen.Count);
        Assert.Contains(seen, e => e.SymbolPath == "GVL.A" && Equals(e.Value, 1));
        Assert.Contains(seen, e => e.SymbolPath == "GVL.B" && Equals(e.Value, 2));
    }

    [Fact]
    public async Task BatchWrite_DoesNotRaiseForRejectedNullEntry()
    {
        // A null entry is rejected per-symbol and never reaches the store, so it is
        // not a write.
        var sim = Create();
        var seen = new List<SimulatedWriteEventArgs>();
        sim.ValueWritten += (_, e) => seen.Add(e);

        await sim.WriteValuesAsync(new Dictionary<string, object?>
        {
            ["GVL.A"] = 1,
            ["GVL.Bad"] = null,
        });

        Assert.Equal("GVL.A", Assert.Single(seen).SymbolPath);
    }

    [Fact]
    public void SetInitialValues_DoesNotRaise()
    {
        // Seeding is silent today (it precedes subscriber registration); a harness that
        // recorded seeds as writes would report its own fixture as SUT behaviour.
        var sim = Create();
        var seen = new List<SimulatedWriteEventArgs>();
        sim.ValueWritten += (_, e) => seen.Add(e);

        sim.SetInitialValues(new Dictionary<string, object?> { ["GVL.Temp"] = 21.5f });

        Assert.Empty(seen);
    }

    [Fact]
    public async Task ThrowingHandler_IsIsolated_AndDoesNotAbortTheWrite()
    {
        var sim = Create();
        var second = 0;
        sim.ValueWritten += (_, _) => throw new InvalidOperationException("boom");
        sim.ValueWritten += (_, _) => second++;

        await sim.WriteValueAsync("GVL.Temp", (object)21.5f);

        Assert.Equal(1, second);
        Assert.Equal(21.5f, await sim.ReadValueAsync<float>("GVL.Temp"));
    }

    [Fact]
    public async Task SubscriptionCallbacksRunBeforeTheEvent()
    {
        // Ordering is stated in the design: the store is updated, subscribers fire,
        // then ValueWritten. A handler that reads the value back sees the written one.
        //
        // NOT an implementation detail, and do not delete this as one: the
        // Dahlke.TwinCAT.Ads.Testing package's write log depends on this exact order.
        // TestPlcTarget buffers every ValueWritten raised inside a harness Write and
        // discards only the LAST entry as the harness's own — which is correct only
        // because every reaction a subscriber makes is appended before the triggering
        // write's own event fires. Reverse these two steps and the harness's own writes
        // start leaking into the log while genuine writes by the code under test are
        // silently dropped.
        var sim = Create();
        var order = new List<string>();
        await sim.SubscribeAsync("GVL.Temp", 100, (_, _) => order.Add("subscriber"));
        sim.ValueWritten += (_, _) => order.Add("event");

        await sim.WriteValueAsync("GVL.Temp", (object)21.5f);

        Assert.Equal(new[] { "subscriber", "event" }, order);
    }
}
