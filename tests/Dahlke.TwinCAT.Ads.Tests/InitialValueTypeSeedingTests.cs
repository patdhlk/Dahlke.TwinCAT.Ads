using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Covers the declared-type form of <see cref="PlcTargetOptions.InitialValues"/>
/// (<c>{ "value": 1500, "type": "DINT" }</c>), which exists because
/// <see cref="IConfiguration"/> is string-typed: without it every config-seeded symbol
/// reaches <see cref="SimulatedAdsConnection"/> as a <see cref="string"/> and a metadata read
/// reports <c>STRING</c> where a real PLC reports <c>DINT</c>/<c>LREAL</c>/<c>BOOL</c>.
/// </summary>
/// <remarks>
/// The end-to-end tests deliberately go config → <c>AddTwinCatAdsSimulation</c> →
/// <see cref="IOptions{TOptions}"/> → the real <c>AdsConnectionFactory</c> → a metadata read,
/// because that is the exact path the bug lived on. Seeding in code (as most of the other
/// simulation tests do) preserves CLR types on its own and cannot catch a regression here.
/// </remarks>
public class InitialValueTypeSeedingTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static TwinCatAdsOptions Resolve(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddTwinCatAdsSimulation(config);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;
    }

    private static OptionsValidationException ResolveExpectingFailure(Dictionary<string, string?> settings)
        => Assert.Throws<OptionsValidationException>(() => Resolve(settings));

    /// <summary>Seeds a simulated connection through the real factory from resolved options.</summary>
    private static IManagedConnection Connect(TwinCatAdsOptions options, string targetId = "sim1")
        => new AdsConnectionFactory(NullLoggerFactory.Instance).Create(targetId, options.Targets[targetId]);

    private static Dictionary<string, string?> Typed(string symbol, string? value, string? type)
    {
        var settings = new Dictionary<string, string?> { ["PlcTargets:sim1:Mode"] = "Simulated" };
        if (value is not null)
            settings[$"PlcTargets:sim1:InitialValues:{symbol}:value"] = value;
        if (type is not null)
            settings[$"PlcTargets:sim1:InitialValues:{symbol}:type"] = type;
        return settings;
    }

    // ------------------------------------------------------------------
    // The reported symptom, end to end
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeclaredTypes_SurviveConfigBinding_AndAreReportedByMetadataRead()
    {
        // The exact table from the bug report: every one of these read back as
        // {"value": "…", "typeName": "STRING"} before the declared-type form existed.
        var options = Resolve(new()
        {
            ["PlcTargets:sim1:Mode"] = "Simulated",
            ["PlcTargets:sim1:InitialValues:MAIN.Speed:value"] = "1500",
            ["PlcTargets:sim1:InitialValues:MAIN.Speed:type"] = "DINT",
            ["PlcTargets:sim1:InitialValues:MAIN.Setpoint:value"] = "21.5",
            ["PlcTargets:sim1:InitialValues:MAIN.Setpoint:type"] = "LREAL",
            ["PlcTargets:sim1:InitialValues:MAIN.Running:value"] = "true",
            ["PlcTargets:sim1:InitialValues:MAIN.Running:type"] = "BOOL",
            ["PlcTargets:sim1:InitialValues:MAIN.Station"] = "Demo Station",
        });

        using var connection = Connect(options);

        var speed = await connection.ReadValueWithMetadataAsync("MAIN.Speed", CancellationToken.None);
        Assert.Equal(1500, speed.Value);
        Assert.IsType<int>(speed.Value);
        Assert.Equal("DINT", speed.TypeName);
        Assert.Equal("Primitive", speed.Category);

        var setpoint = await connection.ReadValueWithMetadataAsync("MAIN.Setpoint", CancellationToken.None);
        Assert.Equal(21.5d, setpoint.Value);
        Assert.IsType<double>(setpoint.Value);
        Assert.Equal("LREAL", setpoint.TypeName);

        var running = await connection.ReadValueWithMetadataAsync("MAIN.Running", CancellationToken.None);
        Assert.Equal(true, running.Value);
        Assert.IsType<bool>(running.Value);
        Assert.Equal("BOOL", running.TypeName);

        // A bare scalar is still a string — correct for a genuine STRING symbol, and the one
        // case where the old behaviour was already right.
        var station = await connection.ReadValueWithMetadataAsync("MAIN.Station", CancellationToken.None);
        Assert.Equal("Demo Station", station.Value);
        Assert.Equal("STRING", station.TypeName);
        Assert.Equal("String", station.Category);
    }

    [Fact]
    public async Task DeclaredType_AlsoFlowsThroughBatchReadsAndNotifications()
    {
        // ReadValuesAsync and notification metadata share InferPlcType, so a correctly typed
        // seed must show up on those surfaces too — not just the single metadata read.
        var options = Resolve(Typed("MAIN.Speed", "1500", "DINT"));
        using var connection = Connect(options);

        var batch = await connection.ReadValuesAsync(["MAIN.Speed"], CancellationToken.None);
        Assert.Equal(1500, batch["MAIN.Speed"].Value);
        Assert.Equal("DINT", batch["MAIN.Speed"].TypeName);

        // A typed read no longer needs Convert.ChangeType to undo a string round-trip.
        Assert.Equal(1500, await connection.ReadValueAsync<int>("MAIN.Speed", CancellationToken.None));
    }

    // ------------------------------------------------------------------
    // Type resolution
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("BOOL", "true", typeof(bool))]
    [InlineData("SINT", "-8", typeof(sbyte))]
    [InlineData("USINT", "8", typeof(byte))]
    [InlineData("BYTE", "8", typeof(byte))]
    [InlineData("INT", "-16", typeof(short))]
    [InlineData("UINT", "16", typeof(ushort))]
    [InlineData("WORD", "16", typeof(ushort))]
    [InlineData("DINT", "-32", typeof(int))]
    [InlineData("UDINT", "32", typeof(uint))]
    [InlineData("DWORD", "32", typeof(uint))]
    [InlineData("LINT", "-64", typeof(long))]
    [InlineData("ULINT", "64", typeof(ulong))]
    [InlineData("LWORD", "64", typeof(ulong))]
    [InlineData("REAL", "1.5", typeof(float))]
    [InlineData("LREAL", "1.5", typeof(double))]
    [InlineData("STRING", "1500", typeof(string))]
    [InlineData("WSTRING", "wide", typeof(string))]
    [InlineData("DT", "2024-05-06T07:08:09", typeof(DateTime))]
    [InlineData("TIME", "00:00:05", typeof(TimeSpan))]
    public void EveryElementaryType_SeedsAsItsClrType(string iecType, string value, Type expected)
    {
        var options = Resolve(Typed("MAIN.X", value, iecType));
        Assert.IsType(expected, options.Targets["sim1"].InitialValues["MAIN.X"]);
    }

    [Fact]
    public void DeclaredType_StringWithNumericContent_StaysAString()
    {
        // The reason the type is never inferred from the content: a genuine STRING symbol
        // whose value happens to parse as a number must not become a DINT.
        var options = Resolve(Typed("MAIN.SerialNo", "1500", "STRING"));
        Assert.Equal("1500", options.Targets["sim1"].InitialValues["MAIN.SerialNo"]);
    }

    [Theory]
    [InlineData("dint")]
    [InlineData("DInt")]
    public void DeclaredType_IsMatchedCaseInsensitively(string iecType)
    {
        var options = Resolve(Typed("MAIN.X", "7", iecType));
        Assert.Equal(7, options.Targets["sim1"].InitialValues["MAIN.X"]);
    }

    [Fact]
    public void DeclaredType_ResolvesBeckhoffAliases()
    {
        // BIT is a Beckhoff alias for BOOL; the seed binder goes through the same lenient
        // tier the rest of the library uses for TwinCAT-reported type names.
        var options = Resolve(Typed("MAIN.Flag", "true", "BIT"));
        Assert.Equal(true, options.Targets["sim1"].InitialValues["MAIN.Flag"]);
    }

    [Fact]
    public void DeclaredType_NumericValue_IsParsedWithInvariantCulture()
    {
        // '.' is the decimal separator regardless of the host's current culture — config files
        // are not localised.
        var options = Resolve(Typed("MAIN.Setpoint", "21.5", "LREAL"));
        Assert.Equal(21.5d, options.Targets["sim1"].InitialValues["MAIN.Setpoint"]);
    }

    // ------------------------------------------------------------------
    // Value-less and null entries
    // ------------------------------------------------------------------

    [Fact]
    public async Task TypeWithoutValue_SeedsTheTypesDefault()
    {
        // Declaring a symbol without a value is how a simulation profile describes the shape
        // of a PLC it is standing in for; the symbol must exist and report its type.
        var options = Resolve(Typed("MAIN.Speed", value: null, type: "DINT"));
        Assert.Equal(0, options.Targets["sim1"].InitialValues["MAIN.Speed"]);

        using var connection = Connect(options);
        var result = await connection.ReadValueWithMetadataAsync("MAIN.Speed", CancellationToken.None);
        Assert.Equal("DINT", result.TypeName);
    }

    [Fact]
    public void ExplicitNullScalar_SeedsNull()
    {
        var options = Resolve(new()
        {
            ["PlcTargets:sim1:Mode"] = "Simulated",
            ["PlcTargets:sim1:InitialValues:MAIN.Sensor"] = null,
        });

        Assert.True(options.Targets["sim1"].InitialValues.ContainsKey("MAIN.Sensor"));
        Assert.Null(options.Targets["sim1"].InitialValues["MAIN.Sensor"]);
    }

    // ------------------------------------------------------------------
    // Misconfiguration — collected, actionable, fail-at-startup
    // ------------------------------------------------------------------

    [Fact]
    public void ValueWithoutType_FailsValidation()
    {
        // The whole point of the feature: an untyped value would silently be seeded as a
        // string, which is the bug. Refuse it rather than reproduce it.
        var ex = ResolveExpectingFailure(new()
        {
            ["PlcTargets:sim1:Mode"] = "Simulated",
            ["PlcTargets:sim1:InitialValues:MAIN.Speed:value"] = "1500",
        });

        Assert.Contains("without a 'type'", ex.Message);
        Assert.Contains("PlcTargets:sim1:InitialValues:MAIN.Speed", ex.Message);
    }

    [Fact]
    public void UnknownTypeName_FailsValidation_AndListsTheSupportedNames()
    {
        var ex = ResolveExpectingFailure(Typed("MAIN.Speed", "1500", "DWORDD"));

        Assert.Contains("'DWORDD'", ex.Message);
        Assert.Contains("not a recognised IEC 61131-3 elementary type", ex.Message);
        Assert.Contains("DINT", ex.Message);
    }

    [Fact]
    public void UnconvertibleValue_FailsValidation()
    {
        var ex = ResolveExpectingFailure(Typed("MAIN.Speed", "not-a-number", "DINT"));

        Assert.Contains("cannot seed value 'not-a-number'", ex.Message);
        Assert.Contains("'DINT'", ex.Message);
    }

    [Fact]
    public void OutOfRangeValue_FailsValidation()
    {
        // 300 does not fit a USINT; Convert.ChangeType raises OverflowException, which the
        // shared converter translates into an InvalidCastException the binder reports.
        var ex = ResolveExpectingFailure(Typed("MAIN.Small", "300", "USINT"));
        Assert.Contains("cannot seed value '300'", ex.Message);
    }

    [Fact]
    public void UnparseableDuration_FailsValidation()
    {
        var ex = ResolveExpectingFailure(Typed("MAIN.Cycle", "5s", "TIME"));
        Assert.Contains("Expected a duration", ex.Message);
    }

    [Fact]
    public void UnrecognisedEntryKey_FailsValidation()
    {
        var ex = ResolveExpectingFailure(new()
        {
            ["PlcTargets:sim1:Mode"] = "Simulated",
            ["PlcTargets:sim1:InitialValues:MAIN.Speed:value"] = "1500",
            ["PlcTargets:sim1:InitialValues:MAIN.Speed:type"] = "DINT",
            ["PlcTargets:sim1:InitialValues:MAIN.Speed:unit"] = "rpm",
        });

        Assert.Contains("unrecognised key 'unit'", ex.Message);
    }

    [Fact]
    public void NonScalarValue_FailsValidation()
    {
        var ex = ResolveExpectingFailure(new()
        {
            ["PlcTargets:sim1:Mode"] = "Simulated",
            ["PlcTargets:sim1:InitialValues:MAIN.Motor:type"] = "DINT",
            ["PlcTargets:sim1:InitialValues:MAIN.Motor:value:Speed"] = "1500",
        });

        Assert.Contains("non-scalar 'value'", ex.Message);
    }

    [Fact]
    public void EveryBadEntry_IsReportedInOneFailure()
    {
        // Aggregated like the rest of the validator: an operator fixing a seed block should
        // not have to restart once per typo.
        var ex = ResolveExpectingFailure(new()
        {
            ["PlcTargets:sim1:Mode"] = "Simulated",
            ["PlcTargets:sim1:InitialValues:MAIN.A:value"] = "1",
            ["PlcTargets:sim1:InitialValues:MAIN.B:value"] = "2",
            ["PlcTargets:sim1:InitialValues:MAIN.B:type"] = "NOPE",
            ["PlcTargets:sim1:InitialValues:MAIN.C:value"] = "x",
            ["PlcTargets:sim1:InitialValues:MAIN.C:type"] = "DINT",
        });

        Assert.Contains("MAIN.A", ex.Message);
        Assert.Contains("MAIN.B", ex.Message);
        Assert.Contains("MAIN.C", ex.Message);
    }

    [Fact]
    public void BadEntry_IsScopedToItsOwnTarget()
    {
        var ex = ResolveExpectingFailure(new()
        {
            ["PlcTargets:good:Mode"] = "Simulated",
            ["PlcTargets:good:InitialValues:MAIN.X:value"] = "1",
            ["PlcTargets:good:InitialValues:MAIN.X:type"] = "DINT",
            ["PlcTargets:bad:Mode"] = "Simulated",
            ["PlcTargets:bad:InitialValues:MAIN.Y:value"] = "1",
        });

        Assert.Contains("Target 'bad'", ex.Message);
        Assert.DoesNotContain("Target 'good'", ex.Message);
    }

    // ------------------------------------------------------------------
    // Interaction with the other registration shapes
    // ------------------------------------------------------------------

    [Fact]
    public void CodeFirstValues_AreNotTouched()
    {
        // Code-first seeding already preserves CLR types; the binder must not reach targets
        // that have no configuration section, nor re-type ones that do.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlcTargets:sim1:Mode"] = "Simulated",
                ["PlcTargets:sim1:InitialValues:MAIN.Speed:value"] = "1500",
                ["PlcTargets:sim1:InitialValues:MAIN.Speed:type"] = "DINT",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddTwinCatAdsSimulation(config, o =>
        {
            // Layered after binding — a code-first target and a code-first addition to a
            // config-bound target both survive.
            o.Targets["sim1"].InitialValues["MAIN.Extra"] = 3.5f;
            o.Targets["sim2"] = new PlcTargetOptions
            {
                InitialValues = { ["MAIN.Temp"] = 21.5f },
            };
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        Assert.Equal(1500, options.Targets["sim1"].InitialValues["MAIN.Speed"]);
        Assert.Equal(3.5f, options.Targets["sim1"].InitialValues["MAIN.Extra"]);
        Assert.Equal(21.5f, options.Targets["sim2"].InitialValues["MAIN.Temp"]);
    }

    [Fact]
    public void RealTargets_AreUnaffected()
    {
        // InitialValues is ignored for a real target, but a declared-type entry on one must
        // still bind (and validate) rather than blow up or be silently dropped.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlcTargets:plc1:AmsNetId"] = "1.2.3.4.5.6",
                ["PlcTargets:plc1:InitialValues:MAIN.Speed:value"] = "1500",
                ["PlcTargets:plc1:InitialValues:MAIN.Speed:type"] = "DINT",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddTwinCatAds(config);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        Assert.Equal(ConnectionMode.Real, options.Targets["plc1"].Mode);
        Assert.Equal(1500, options.Targets["plc1"].InitialValues["MAIN.Speed"]);
    }
}
