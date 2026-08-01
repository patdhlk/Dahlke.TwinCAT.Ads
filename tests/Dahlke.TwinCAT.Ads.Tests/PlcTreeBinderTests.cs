using Microsoft.Extensions.Logging.Abstractions;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Binding a decoded PLC value tree onto a .NET type by member name — what makes a simulated
/// target able to stand in for hardware on a struct read, and what lets
/// <see cref="AdsValueResult.GetValue{T}"/> turn a real connection's metadata or batch result
/// into a domain type instead of a dictionary the caller destructures by hand.
///
/// Coverage:
/// - Shapes that must bind: positional records, mutable classes, structs, nested trees, arrays,
///   and members needing the same widening a scalar read performs.
/// - Case-insensitive member matching, because PLC member names follow the PLC program's
///   conventions and not C#'s.
/// - The strictness rule: every member of the TARGET must be present in the tree, and a missing
///   one fails naming it rather than defaulting silently. Extra tree keys are ignored.
/// - The one thing this deliberately cannot catch — member ORDER — which is what a real symbol
///   read binds by. That gap is pinned here so it is a decision on record, not a surprise.
/// </summary>
public class PlcTreeBinderTests
{
    private static SimulatedAdsConnection CreateSim()
        => new("plc1", "PLC One", NullLoggerFactory.Instance);

    private static Dictionary<string, object?> Motor(int speed = 1500, bool running = true)
        => new() { ["Speed"] = speed, ["Running"] = running };

    // Positional record — the immutable shape, bound through its constructor.
    public record MotorState(int Speed, bool Running);

    // Mutable class — bound through the parameterless constructor and property setters.
    public class MutableMotor
    {
        public int Speed { get; set; }
        public bool Running { get; set; }
    }

    public struct MotorStruct
    {
        public int Speed { get; set; }
        public bool Running { get; set; }
    }

    public record Machine(string Name, MotorState Motor);

    public class WithComputed
    {
        public int Speed { get; set; }
        public bool IsFast => Speed > 1000;      // no setter — nothing to disagree about
    }

    // =========================================================================
    // Shapes that bind
    // =========================================================================

    [Fact]
    public async Task PositionalRecord_BindsThroughItsConstructor()
    {
        using var sim = CreateSim();
        sim.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Motor"] = Motor() });

        var motor = await sim.ReadValueAsync<MotorState>("MAIN.Motor");

        Assert.Equal(new MotorState(1500, true), motor);
    }

    [Fact]
    public async Task MutableClass_BindsThroughItsSetters()
    {
        using var sim = CreateSim();
        sim.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Motor"] = Motor(900, false) });

        var motor = await sim.ReadValueAsync<MutableMotor>("MAIN.Motor");

        Assert.Equal(900, motor.Speed);
        Assert.False(motor.Running);
    }

    [Fact]
    public async Task Struct_Binds()
    {
        using var sim = CreateSim();
        sim.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Motor"] = Motor() });

        var motor = await sim.ReadValueAsync<MotorStruct>("MAIN.Motor");

        Assert.Equal(1500, motor.Speed);
    }

    [Fact]
    public async Task NestedTree_BindsAllTheWayDown()
    {
        using var sim = CreateSim();
        sim.SetInitialValues(new Dictionary<string, object?>
        {
            ["MAIN.Machine"] = new Dictionary<string, object?>
            {
                ["Name"] = "Press 1",
                ["Motor"] = Motor(750, false),
            },
        });

        var machine = await sim.ReadValueAsync<Machine>("MAIN.Machine");

        Assert.Equal("Press 1", machine.Name);
        Assert.Equal(new MotorState(750, false), machine.Motor);
    }

    [Fact]
    public async Task Array_BindsElementwise()
    {
        using var sim = CreateSim();
        sim.SetInitialValues(new Dictionary<string, object?>
        {
            ["MAIN.Motors"] = new object?[] { Motor(100), Motor(200, false) },
        });

        var motors = await sim.ReadValueAsync<MotorState[]>("MAIN.Motors");

        Assert.Equal([new MotorState(100, true), new MotorState(200, false)], motors);
    }

    [Fact]
    public async Task ScalarArray_Binds()
    {
        using var sim = CreateSim();
        sim.SetInitialValues(new Dictionary<string, object?>
        {
            ["MAIN.Speeds"] = new object?[] { 1, 2, 3 },
        });

        var speeds = await sim.ReadValueAsync<int[]>("MAIN.Speeds");

        Assert.Equal([1, 2, 3], speeds);
    }

    [Fact]
    public async Task Members_GetTheSameWideningAScalarReadWouldGet()
    {
        using var sim = CreateSim();
        // A PLC INT stored as int, read into a double member — the widening a scalar read does.
        sim.SetInitialValues(new Dictionary<string, object?>
        {
            ["MAIN.Motor"] = new Dictionary<string, object?> { ["Speed"] = 1500, ["Running"] = "true" },
        });

        var motor = await sim.ReadValueAsync<WideningMotor>("MAIN.Motor");

        Assert.Equal(1500d, motor.Speed);
        Assert.True(motor.Running);      // "true" -> bool, invariant-culture, as a scalar read does
    }

    public record WideningMotor(double Speed, bool Running);

    [Fact]
    public async Task MemberNames_MatchCaseInsensitively()
    {
        using var sim = CreateSim();
        sim.SetInitialValues(new Dictionary<string, object?>
        {
            // PLC naming conventions, not C#'s.
            ["MAIN.Motor"] = new Dictionary<string, object?> { ["speed"] = 1500, ["RUNNING"] = true },
        });

        var motor = await sim.ReadValueAsync<MotorState>("MAIN.Motor");

        Assert.Equal(1500, motor.Speed);
        Assert.True(motor.Running);
    }

    [Fact]
    public async Task ReadOnlyComputedProperties_AreNotRequired()
    {
        using var sim = CreateSim();
        sim.SetInitialValues(new Dictionary<string, object?>
        {
            ["MAIN.Motor"] = new Dictionary<string, object?> { ["Speed"] = 1500 },
        });

        var motor = await sim.ReadValueAsync<WithComputed>("MAIN.Motor");

        Assert.True(motor.IsFast);
    }

    [Fact]
    public async Task ExactClrTypeInTheStore_StillShortCircuits()
    {
        using var sim = CreateSim();
        var seeded = new MotorState(42, false);
        sim.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Motor"] = seeded });

        // Seeded as the type itself: the direct-cast fast path, no binding involved.
        Assert.Same(seeded, await sim.ReadValueAsync<MotorState>("MAIN.Motor"));
    }

    // =========================================================================
    // Strictness — a disagreement is loud, not defaulted
    // =========================================================================

    [Fact]
    public async Task MissingMember_FailsNamingIt_RatherThanDefaulting()
    {
        using var sim = CreateSim();
        sim.SetInitialValues(new Dictionary<string, object?>
        {
            ["MAIN.Motor"] = new Dictionary<string, object?> { ["Speed"] = 1500 },   // no Running
        });

        var ex = await Assert.ThrowsAsync<InvalidCastException>(
            () => sim.ReadValueAsync<MotorState>("MAIN.Motor"));

        Assert.Contains("'Running'", ex.Message);
        Assert.Contains("MAIN.Motor", ex.Message);
        // The message lists what the value DOES have, so the fix does not need a debugger.
        Assert.Contains("'Speed'", ex.Message);
    }

    [Fact]
    public async Task MisspelledMemberOnTheTargetType_IsCaughtAsAMissingMember()
    {
        using var sim = CreateSim();
        sim.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Motor"] = Motor() });

        var ex = await Assert.ThrowsAsync<InvalidCastException>(
            () => sim.ReadValueAsync<TypoedMotor>("MAIN.Motor"));

        Assert.Contains("'Speeed'", ex.Message);
    }

    public record TypoedMotor(int Speeed, bool Running);

    [Fact]
    public async Task ExtraKeysInTheTree_AreIgnored_TheTargetTypeDrives()
    {
        using var sim = CreateSim();
        sim.SetInitialValues(new Dictionary<string, object?>
        {
            ["MAIN.Motor"] = new Dictionary<string, object?>
            {
                ["Speed"] = 1500,
                ["Running"] = true,
                ["Torque"] = 12,        // the PLC has it, the target type does not
            },
        });

        var motor = await sim.ReadValueAsync<MotorState>("MAIN.Motor");

        Assert.Equal(new MotorState(1500, true), motor);
    }

    [Fact]
    public async Task UnbindableMemberValue_FailsNamingTheMemberPath()
    {
        using var sim = CreateSim();
        sim.SetInitialValues(new Dictionary<string, object?>
        {
            ["MAIN.Motor"] = new Dictionary<string, object?> { ["Speed"] = "not a number", ["Running"] = true },
        });

        var ex = await Assert.ThrowsAsync<InvalidCastException>(
            () => sim.ReadValueAsync<MotorState>("MAIN.Motor"));

        // The member path, not just the symbol — which member disagreed is the whole question.
        Assert.Contains("MAIN.Motor.Speed", ex.Message);
    }

    [Fact]
    public async Task TypeWithNoBindableMembers_IsRefused()
    {
        using var sim = CreateSim();
        sim.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Motor"] = Motor() });

        await Assert.ThrowsAsync<InvalidCastException>(
            () => sim.ReadValueAsync<NoMembers>("MAIN.Motor"));
    }

    public class NoMembers { }

    // =========================================================================
    // The documented gap: order is not checked, because a tree has no order
    // =========================================================================

    [Fact]
    public async Task MemberOrderIsNotChecked_WhichIsTheLimitOfWhatSimulationCanProve()
    {
        using var sim = CreateSim();
        sim.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Motor"] = Motor() });

        // Same names, declared in the opposite order. A decoded tree is keyed by name and has no
        // order to disagree with, so this binds — while a REAL ReadValueAsync<T> maps PLC memory
        // by declaration order through Beckhoff's marshaller and would not agree.
        //
        // This test exists to keep that asymmetry a decision on record: a simulated target catches
        // a misspelled or mistyped member, and cannot catch a mis-ordered one.
        var reordered = await sim.ReadValueAsync<ReorderedMotor>("MAIN.Motor");

        Assert.Equal(1500, reordered.Speed);
        Assert.True(reordered.Running);
    }

    public record ReorderedMotor(bool Running, int Speed);

    // =========================================================================
    // The same binding through a batch/metadata result, which is the path a REAL
    // connection shares — its struct reads already decode to this same tree.
    // =========================================================================

    [Fact]
    public void AdsValueResult_GetValue_BindsATreeOntoADomainType()
    {
        // Exactly the shape ReadValueWithMetadataAsync and ReadValuesAsync produce for a struct on
        // a real connection, so this is the real path's half of the fix.
        var result = AdsValueResult.Success(Motor(1200), "ST_Motor");

        var motor = result.GetValue<MotorState>();

        Assert.Equal(new MotorState(1200, true), motor);
    }

    [Fact]
    public async Task BatchRead_BindsEachStructResult()
    {
        using var sim = CreateSim();
        sim.SetInitialValues(new Dictionary<string, object?>
        {
            ["MAIN.A"] = Motor(10),
            ["MAIN.B"] = Motor(20, false),
        });

        var results = await sim.ReadValuesAsync(["MAIN.A", "MAIN.B"]);

        Assert.Equal(new MotorState(10, true), results["MAIN.A"].GetValue<MotorState>());
        Assert.Equal(new MotorState(20, false), results["MAIN.B"].GetValue<MotorState>());
    }

    // =========================================================================
    // Typed subscriptions convert through the same core
    // =========================================================================

    [Fact]
    public async Task TypedSubscription_DeliversABoundStruct()
    {
        using var sim = CreateSim();
        var seen = new List<MotorState?>();

        using var sub = await sim.SubscribeAsync<MotorState>("MAIN.Motor", 100, (_, v) => seen.Add(v));
        await sim.WriteValueAsync("MAIN.Motor", Motor(640, false));

        Assert.Equal([new MotorState(640, false)], seen);
    }
}
