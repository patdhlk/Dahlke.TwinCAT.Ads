using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Alarms.Tests;

/// <summary>
/// Tests which <see cref="IValidateOptions{TOptions}"/> implementations
/// <c>AddTwinCatAdsAlarms</c> registers, and what they collectively report.
/// </summary>
/// <remarks>
/// Container-level rather than unit-level on purpose. Since 0.8.0 the acknowledge rules live
/// with the dialect that reads them, so "does a misconfiguration still fail the boot" is a
/// question about which validators the container holds — it cannot be answered by calling one
/// validator directly. Resolving <c>IOptions&lt;PlcAlarmsOptions&gt;.Value</c> runs all of them
/// and concatenates their failures, which is exactly the path a host takes at startup.
/// </remarks>
public class AlarmValidationRegistrationTests
{
    /// <summary>
    /// Builds a container wired as a real application wires one.
    /// </summary>
    /// <param name="coreTargets">
    /// Target ids to configure under the core library, i.e. what <c>PlcTargets</c> would hold.
    /// The neutral validator cross-references alarm targets against these.
    /// </param>
    /// <param name="alarmTargets">The <c>PlcAlarms:Targets</c> entries to validate.</param>
    /// <param name="configure">
    /// Runs BEFORE <c>AddTwinCatAdsAlarms</c>, so a test can register its own dialect and have
    /// it win — the ordering the package documents, and which now also decides whether the
    /// built-in dialect's validator is registered at all.
    /// </param>
    private static ServiceProvider BuildContainer(
        string[] coreTargets,
        Dictionary<string, PlcAlarmTargetOptions> alarmTargets,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();

        services.AddTwinCatAdsSimulation(o =>
        {
            for (var i = 0; i < coreTargets.Length; i++)
            {
                // Distinct per target — two PLCs never share an AMS Net ID, and the core
                // validator enforces it.
                o.Targets[coreTargets[i]] = new PlcTargetOptions { AmsNetId = $"1.2.3.4.5.{i + 1}" };
            }
        });

        configure?.Invoke(services);

        services.AddTwinCatAdsAlarms(new ConfigurationBuilder().Build());

        // After AddTwinCatAdsAlarms' own binding delegate, so this is what the options carry.
        services.Configure<PlcAlarmsOptions>(o => o.Targets = alarmTargets);

        return services.BuildServiceProvider();
    }

    private static Dictionary<string, PlcAlarmTargetOptions> Target(string plcId, string symbolPath) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [plcId] = new PlcAlarmTargetOptions { SymbolPath = symbolPath, CycleTimeMs = 200 },
        };

    /// <summary>Forces validation the way a host's ValidateOnStart does, and returns what broke.</summary>
    private static OptionsValidationException Failures(ServiceProvider provider) =>
        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<PlcAlarmsOptions>>().Value);

    [Fact]
    public void NoDialectRegistered_RegistersTheBuiltInDialectAndItsValidator()
    {
        using var provider = BuildContainer(["plc1"], Target("plc1", "MAIN.ErrorHandler.aHmiAlarms"));

        Assert.IsType<ErrorHandlerAlarmDialect>(provider.GetRequiredService<IPlcAlarmDialect>());

        var validators = provider.GetServices<IValidateOptions<PlcAlarmsOptions>>().ToList();

        Assert.Contains(validators, v => v is PlcAlarmsOptionsValidator);
        Assert.Contains(validators, v => v is ErrorHandlerAlarmDialectOptionsValidator);
    }

    [Fact]
    public void CustomDialect_DoesNotGetTheBuiltInDialectsRules()
    {
        // This is issue #25. A dialect that acknowledges by some other mechanism has no use for
        // an instance path, and under 0.7.0 a SymbolPath with no parent segment failed its boot
        // anyway — escapable only by setting AcknowledgeInstancePath to a value nothing read.
        using var provider = BuildContainer(
            ["plc1"],
            Target("plc1", "Alarms"),   // no dot — nothing to trim
            services => services.AddSingleton<IPlcAlarmDialect, StubDialect>());

        var options = provider.GetRequiredService<IOptions<PlcAlarmsOptions>>().Value;

        Assert.Equal("Alarms", options.Targets["plc1"].SymbolPath);

        Assert.DoesNotContain(
            provider.GetServices<IValidateOptions<PlcAlarmsOptions>>(),
            v => v is ErrorHandlerAlarmDialectOptionsValidator);
    }

    [Fact]
    public void CustomDialect_StillGetsTheVendorNeutralRules()
    {
        // The dialect owns how its PLC acknowledges. It does not own whether an alarm array was
        // named at all — that rule holds for every dialect, and losing it here would trade one
        // over-broad validator for no validator.
        using var provider = BuildContainer(
            ["plc1"],
            Target("plc1", "   "),
            services => services.AddSingleton<IPlcAlarmDialect, StubDialect>());

        var failures = Failures(provider);

        Assert.Contains(failures.Failures, f => f.Contains("SymbolPath", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryFailure_IsReportedAtOnce_AcrossBothValidators()
    {
        // Moved here from PlcAlarmsOptionsValidatorTests, where it could no longer be proven:
        // the four failures now come from two validators. Four, not three, because a blank
        // SymbolPath is BOTH "no alarm array named" and "no parent segment to derive the
        // acknowledging function block from" — both true, both separately fixable, and an
        // operator who fills in a bare 'Alarms' has fixed one and not the other.
        var alarmTargets = new Dictionary<string, PlcAlarmTargetOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["plc1"] = new() { SymbolPath = "", CycleTimeMs = 0 },
            ["ghost"] = new() { SymbolPath = "GVL.Errors", CycleTimeMs = 200 },
        };

        using var provider = BuildContainer(["plc1"], alarmTargets);

        var failures = Failures(provider);

        Assert.Equal(4, failures.Failures.Count());
    }

    [Fact]
    public void AConsumersOwnValidator_RunsAlongsideTheBuiltInOnes()
    {
        // Under TryAddSingleton this suppressed every built-in rule, because that overload adds
        // only when no descriptor for the service type exists at all. Someone adding one rule
        // lost all of ours, silently, and a blank SymbolPath booted clean.
        using var provider = BuildContainer(
            ["plc1"],
            Target("plc1", "   "),
            services => services.AddSingleton<IValidateOptions<PlcAlarmsOptions>, RejectingValidator>());

        var failures = Failures(provider);

        Assert.Contains(failures.Failures, f => f.Contains("SymbolPath", StringComparison.Ordinal));
        Assert.Contains(failures.Failures, f => f.Contains(RejectingValidator.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void CalledTwice_RegistersOneDialectAndOneOfEachValidator()
    {
        var services = new ServiceCollection();

        services.AddTwinCatAdsSimulation(o =>
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.1" });

        services.AddTwinCatAdsAlarms(new ConfigurationBuilder().Build());
        services.AddTwinCatAdsAlarms(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<IPlcAlarmDialect>());

        var validators = provider.GetServices<IValidateOptions<PlcAlarmsOptions>>().ToList();

        Assert.Single(validators, v => v is PlcAlarmsOptionsValidator);
        Assert.Single(validators, v => v is ErrorHandlerAlarmDialectOptionsValidator);
    }

    /// <summary>
    /// Stands in for a consumer's dialect that acknowledges by some mechanism of its own and
    /// therefore reads neither acknowledge member. Never invoked — these tests only ever ask
    /// which services the container holds.
    /// </summary>
    private sealed class StubDialect : IPlcAlarmDialect
    {
        public IReadOnlyList<PlcAlarm> Bind(AlarmBindContext context) => [];

        public Task<bool> AcknowledgeAsync(AlarmAcknowledgeContext context, CancellationToken ct) =>
            Task.FromResult(true);
    }

    /// <summary>A consumer's own validator, which must add to the built-in rules rather than replace them.</summary>
    private sealed class RejectingValidator : IValidateOptions<PlcAlarmsOptions>
    {
        public const string Message = "A rule of the consumer's own.";

        public ValidateOptionsResult Validate(string? name, PlcAlarmsOptions options) =>
            ValidateOptionsResult.Fail(Message);
    }
}
