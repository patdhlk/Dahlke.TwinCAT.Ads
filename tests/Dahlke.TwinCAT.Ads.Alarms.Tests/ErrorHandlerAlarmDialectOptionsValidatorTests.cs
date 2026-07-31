namespace Dahlke.TwinCAT.Ads.Alarms.Tests;

/// <summary>
/// Unit tests for <see cref="ErrorHandlerAlarmDialectOptionsValidator"/> — the rules that
/// belong to the built-in <c>FB_ErrorHandler</c> dialect rather than to alarm monitoring in
/// general.
/// </summary>
/// <remarks>
/// These three cases moved here from <see cref="PlcAlarmsOptionsValidatorTests"/> unchanged.
/// That they still pass against a validator holding nothing else is the point: the rules never
/// needed the vendor-neutral options, only the two members the dialect reads.
/// </remarks>
public class ErrorHandlerAlarmDialectOptionsValidatorTests
{
    private static PlcAlarmsOptions ValidOptions() => new()
    {
        Targets = new Dictionary<string, PlcAlarmTargetOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["plc1"] = new() { SymbolPath = "GVL.Errors", CycleTimeMs = 200 },
        },
    };

    private static readonly ErrorHandlerAlarmDialectOptionsValidator Validator = new();

    [Fact]
    public void ValidOptions_Succeed()
    {
        var result = Validator.Validate(null, ValidOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void BlankAcknowledgeMethod_Fails()
    {
        var options = ValidOptions();
        options.Targets["plc1"].AcknowledgeMethod = "  ";

        var result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("AcknowledgeMethod", StringComparison.Ordinal));
    }

    [Fact]
    public void UnderivableInstancePath_Fails()
    {
        var options = ValidOptions();
        options.Targets["plc1"].SymbolPath = "Alarms";   // no dot — nothing to trim

        var result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!,
            f => f.Contains("AcknowledgeInstancePath", StringComparison.Ordinal));
    }

    [Fact]
    public void UnderivableInstancePath_WithAnExplicitOverride_Succeeds()
    {
        var options = ValidOptions();
        options.Targets["plc1"].SymbolPath = "Alarms";
        options.Targets["plc1"].AcknowledgeInstancePath = "GVL.Handler";

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void UnderivableInstancePath_NamesTheOrderingFix()
    {
        // The escape hatch changed shape in 0.8.0. Under 0.7.0 a custom dialect satisfied this
        // rule with any non-blank value, passed through unread; now it registers itself before
        // AddTwinCatAdsAlarms and this validator is never added at all. An operator meets that
        // change as a boot failure, not as a doc, so the message has to carry it — and this
        // asserts the guidance cannot be dropped by a later edit without a test going red.
        var options = ValidOptions();
        options.Targets["plc1"].SymbolPath = "Alarms";

        var result = Validator.Validate(null, options);

        var failure = Assert.Single(result.Failures!);
        Assert.Contains("FB_ErrorHandler", failure, StringComparison.Ordinal);
        Assert.Contains("BEFORE calling AddTwinCatAdsAlarms", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankAcknowledgeMethod_NamesTheOrderingFix()
    {
        // AcknowledgeMethod defaults to 'AcknowledgeAlarm' — vocabulary a consumer with a custom
        // dialect could plausibly try to satisfy, unlike AcknowledgeInstancePath's null default,
        // which they would never touch. That makes this the rule most likely to trip a
        // mis-ordered consumer, so it carries the same ordering guidance as the instance-path
        // failure, and this asserts that guidance cannot be dropped by a later edit without a
        // test going red.
        var options = ValidOptions();
        options.Targets["plc1"].AcknowledgeMethod = "  ";

        var result = Validator.Validate(null, options);

        var failure = Assert.Single(result.Failures!);
        Assert.Contains("FB_ErrorHandler", failure, StringComparison.Ordinal);
        Assert.Contains("BEFORE calling AddTwinCatAdsAlarms", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void NullTargets_Succeeds()
    {
        // Same contract as the vendor-neutral validator: a code-first caller may assign null,
        // which means "no alarm targets configured" and is exactly as legal as an empty
        // dictionary. It must not throw.
        var options = new PlcAlarmsOptions { Targets = null! };

        var result = Validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void EveryFailure_IsReportedAtOnce()
    {
        // One boot, one complete picture — the same contract PlcAlarmsOptionsValidator keeps.
        // A validator that returned on its first failure would make an operator fix a blank
        // AcknowledgeMethod, restart, and only then learn about the instance path.
        var options = new PlcAlarmsOptions
        {
            Targets = new Dictionary<string, PlcAlarmTargetOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["plc1"] = new() { SymbolPath = "Alarms", AcknowledgeMethod = "" },
            },
        };

        var result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Equal(2, result.Failures!.Count());
    }
}
