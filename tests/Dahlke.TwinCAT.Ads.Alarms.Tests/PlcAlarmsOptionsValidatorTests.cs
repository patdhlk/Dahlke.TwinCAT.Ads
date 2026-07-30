using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Alarms.Tests;

/// <summary>
/// Unit tests for <see cref="PlcAlarmsOptionsValidator"/>. Every misconfiguration
/// must be reported in ONE startup failure, following the core library's validator.
/// </summary>
public class PlcAlarmsOptionsValidatorTests
{
    private static PlcAlarmsOptionsValidator ValidatorFor(params string[] configuredTargets)
    {
        var ads = new TwinCatAdsOptions
        {
            Targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase),
        };

        foreach (var id in configuredTargets)
            ads.Targets[id] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };

        return new PlcAlarmsOptionsValidator(Options.Create(ads));
    }

    private static PlcAlarmsOptions ValidOptions() => new()
    {
        Targets = new Dictionary<string, PlcAlarmTargetOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["plc1"] = new() { SymbolPath = "GVL.Errors", CycleTimeMs = 200 },
        },
    };

    [Fact]
    public void ValidOptions_Succeed()
    {
        var result = ValidatorFor("plc1").Validate(null, ValidOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void NoTargets_Succeeds()
    {
        // The package is opt-in per target: registering it without configuring any
        // alarm array is a no-op, not an error.
        var result = ValidatorFor("plc1").Validate(null, new PlcAlarmsOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void UnknownPlcId_Fails()
    {
        var options = ValidOptions();

        var result = ValidatorFor("someOtherPlc").Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("plc1", StringComparison.Ordinal));
    }

    [Fact]
    public void BlankSymbolPath_Fails()
    {
        var options = ValidOptions();
        options.Targets["plc1"].SymbolPath = "   ";

        var result = ValidatorFor("plc1").Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("SymbolPath", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveCycleTime_Fails(int cycleTimeMs)
    {
        var options = ValidOptions();
        options.Targets["plc1"].CycleTimeMs = cycleTimeMs;

        var result = ValidatorFor("plc1").Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("CycleTimeMs", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryFailure_IsReportedAtOnce()
    {
        var options = new PlcAlarmsOptions
        {
            Targets = new Dictionary<string, PlcAlarmTargetOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["plc1"] = new() { SymbolPath = "", CycleTimeMs = 0 },
                ["ghost"] = new() { SymbolPath = "GVL.Errors", CycleTimeMs = 200 },
            },
        };

        var result = ValidatorFor("plc1").Validate(null, options);

        Assert.True(result.Failed);
        Assert.Equal(3, result.Failures!.Count());
    }
}
