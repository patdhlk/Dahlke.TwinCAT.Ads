using Dahlke.TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Testing.Tests;

/// <summary>
/// Pins the assertions. The failure MESSAGES carry as much weight as the pass/fail:
/// value equality here is type-sensitive (a boxed Single 23.5 is not a boxed Double
/// 23.5), which is the single likeliest reason a correct-looking assertion fails, so the
/// message has to name the types rather than print two identical-looking numbers.
/// </summary>
public class TestPlcAssertionTests
{
    private static async Task<TestPlc> StartAsync() =>
        await TestPlc.Create().WithTarget("plc1").StartAsync();

    [Fact]
    public async Task AssertWritten_PassesOnAnExactMatch()
    {
        await using var plc = await StartAsync();
        await plc.Connection("plc1").WriteValueAsync("GVL.Setpoint", 23.5f);

        plc.Target("plc1").AssertWritten("GVL.Setpoint", 23.5f);
        plc.Target("plc1").AssertWritten("GVL.Setpoint");
    }

    [Fact]
    public async Task AssertWritten_FailsOnATypeMismatch_AndNamesBothTypes()
    {
        await using var plc = await StartAsync();
        await plc.Connection("plc1").WriteValueAsync("GVL.Setpoint", 23.5d);

        var ex = Assert.Throws<PlcAssertionException>(
            () => plc.Target("plc1").AssertWritten("GVL.Setpoint", 23.5f));

        Assert.Contains("Single", ex.Message);
        Assert.Contains("Double", ex.Message);
        Assert.Contains("GVL.Setpoint", ex.Message);
        Assert.Contains("plc1", ex.Message);
    }

    [Fact]
    public async Task AssertWritten_FailsWhenNothingWasWritten_AndSaysSo()
    {
        await using var plc = await StartAsync();

        var ex = Assert.Throws<PlcAssertionException>(
            () => plc.Target("plc1").AssertWritten("GVL.Setpoint", 23.5f));

        Assert.Contains("no writes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssertWritten_ListsEveryRecordedWriteOnFailure()
    {
        await using var plc = await StartAsync();
        var conn = plc.Connection("plc1");
        await conn.WriteValueAsync("GVL.Setpoint", 20f);
        await conn.WriteValueAsync("GVL.Setpoint", 21f);

        var ex = Assert.Throws<PlcAssertionException>(
            () => plc.Target("plc1").AssertWritten("GVL.Setpoint", 23.5f));

        Assert.Contains("20", ex.Message);
        Assert.Contains("21", ex.Message);
    }

    [Fact]
    public async Task AssertWritten_IgnoresAHarnessWrite()
    {
        // The rule the whole design turns on, asserted at the level a consumer sees.
        await using var plc = await StartAsync();
        plc.Target("plc1").Write("GVL.Setpoint", 23.5f);

        Assert.Throws<PlcAssertionException>(
            () => plc.Target("plc1").AssertWritten("GVL.Setpoint", 23.5f));
    }

    [Fact]
    public async Task AssertNotWritten_PassesWhenNothingWasWritten_FailsWhenSomethingWas()
    {
        await using var plc = await StartAsync();
        plc.Target("plc1").AssertNotWritten("GVL.Setpoint");

        await plc.Connection("plc1").WriteValueAsync("GVL.Setpoint", 23.5f);

        var ex = Assert.Throws<PlcAssertionException>(
            () => plc.Target("plc1").AssertNotWritten("GVL.Setpoint"));
        Assert.Contains("23.5", ex.Message);
    }

    [Fact]
    public async Task AssertWriteCount_PassesOnTheExactCount()
    {
        await using var plc = await StartAsync();
        var conn = plc.Connection("plc1");
        await conn.WriteValueAsync("GVL.Setpoint", 20f);
        await conn.WriteValueAsync("GVL.Setpoint", 21f);

        plc.Target("plc1").AssertWriteCount("GVL.Setpoint", 2);

        var ex = Assert.Throws<PlcAssertionException>(
            () => plc.Target("plc1").AssertWriteCount("GVL.Setpoint", 3));
        Assert.Contains("3", ex.Message);
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public async Task AssertWritten_MatchesANullExpectedValueOnlyAgainstNull()
    {
        // A null cannot be WRITTEN (the real path rejects it), so this documents that
        // asserting null never spuriously matches a real write.
        await using var plc = await StartAsync();
        await plc.Connection("plc1").WriteValueAsync("GVL.Setpoint", 23.5f);

        Assert.Throws<PlcAssertionException>(
            () => plc.Target("plc1").AssertWritten("GVL.Setpoint", null));
    }
}
