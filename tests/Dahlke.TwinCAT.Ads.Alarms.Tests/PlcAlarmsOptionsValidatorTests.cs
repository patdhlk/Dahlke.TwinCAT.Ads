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
    public void NullTargets_Succeeds()
    {
        // Normal JSON binding always produces at least an empty dictionary, but the
        // constructor is public and a code-first caller can assign null explicitly.
        // That is exactly as legal as an empty dictionary — no alarm targets
        // configured — so it must succeed, not throw.
        var options = new PlcAlarmsOptions { Targets = null! };

        var result = ValidatorFor("plc1").Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void CoreOptionsBroken_StillReportsAlarmFailures()
    {
        // IOptions<TwinCatAdsOptions>.Value re-runs TwinCatAdsOptionsValidator and
        // throws OptionsValidationException when the core options are themselves
        // invalid. A boot where both option sets are misconfigured is the common
        // case (someone setting up alarms for the first time is usually setting up
        // targets for the first time too), so that must not erase the alarm-specific
        // failures below — they still need to reach the operator in this same pass.
        var options = new PlcAlarmsOptions
        {
            Targets = new Dictionary<string, PlcAlarmTargetOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["plc1"] = new() { SymbolPath = "   ", CycleTimeMs = 0 },
            },
        };

        var validator = new PlcAlarmsOptionsValidator(new ThrowingCoreOptions());

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("SymbolPath", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains("CycleTimeMs", StringComparison.Ordinal));
    }

    /// <summary>
    /// Stands in for the core's <c>IOptions&lt;TwinCatAdsOptions&gt;</c> when
    /// <c>TwinCatAdsOptions</c> itself fails validation: <c>Value</c> re-runs the
    /// core validator and throws on every access, exactly like the real
    /// <see cref="OptionsFactory{TOptions}"/> does for invalid options.
    /// <see cref="Options.Create{TOptions}"/> cannot reproduce that, so this is a
    /// minimal hand-written stub.
    /// </summary>
    private sealed class ThrowingCoreOptions : IOptions<TwinCatAdsOptions>
    {
        public TwinCatAdsOptions Value =>
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(TwinCatAdsOptions),
                ["Target 'plc1': AmsNetId is required."]);
    }
}
