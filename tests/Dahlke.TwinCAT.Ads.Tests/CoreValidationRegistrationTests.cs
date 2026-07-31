using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Tests which <see cref="IValidateOptions{TOptions}"/> implementations the
/// <c>AddTwinCatAds</c> / <c>AddTwinCatAdsSimulation</c> overloads register, and what a
/// container holding several of them collectively reports.
/// </summary>
/// <remarks>
/// Container-level rather than unit-level on purpose: "does a malformed AmsNetId still fail the
/// boot once a consumer has registered a validator of their own" is a question about which
/// validators the container holds, and cannot be answered by calling
/// <see cref="TwinCatAdsOptionsValidator"/> directly. Resolving
/// <c>IOptions&lt;TwinCatAdsOptions&gt;.Value</c> runs every registered validator and
/// concatenates their failures, which is the path a host takes at startup.
/// </remarks>
public class CoreValidationRegistrationTests
{
    /// <summary>A malformed Net ID — five octets, not six. The rule the core validator exists for.</summary>
    private const string MalformedNetId = "1.2.3.4.5";

    /// <summary>Forces validation the way a host's ValidateOnStart does, and returns what broke.</summary>
    private static OptionsValidationException Failures(ServiceProvider provider) =>
        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value);

    private static IConfiguration ConfigurationWith(string plcId, string amsNetId) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"PlcTargets:{plcId}:AmsNetId"] = amsNetId,
            })
            .Build();

    [Fact]
    public void CodeFirst_AConsumersOwnValidator_RunsAlongsideTheBuiltInOne()
    {
        // Issue #29. Under TryAddSingleton this suppressed every built-in rule, because that
        // overload adds only when no descriptor for the service type exists at all. Someone
        // adding one rule lost all of ours, silently, and a malformed AmsNetId booted clean —
        // surfacing later as a connection error that points at the network, not at config.
        var services = new ServiceCollection();

        services.AddSingleton<IValidateOptions<TwinCatAdsOptions>, RejectingValidator>();

        services.AddTwinCatAds(o =>
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = MalformedNetId });

        using var provider = services.BuildServiceProvider();

        var failures = Failures(provider);

        Assert.Contains(failures.Failures, f => f.Contains("AmsNetId", StringComparison.Ordinal));
        Assert.Contains(failures.Failures, f => f.Contains(RejectingValidator.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void Configuration_AConsumersOwnValidator_RunsAlongsideTheBuiltInOne()
    {
        // The config-bound overloads reach the validator through a different helper
        // (BindTwinCatAdsOptions), which had the same TryAddSingleton call and so the same defect.
        var services = new ServiceCollection();

        services.AddSingleton<IValidateOptions<TwinCatAdsOptions>, RejectingValidator>();

        services.AddTwinCatAds(ConfigurationWith("plc1", MalformedNetId));

        using var provider = services.BuildServiceProvider();

        var failures = Failures(provider);

        Assert.Contains(failures.Failures, f => f.Contains("AmsNetId", StringComparison.Ordinal));
        Assert.Contains(failures.Failures, f => f.Contains(RejectingValidator.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void Simulation_AConsumersOwnValidator_RunsAlongsideTheBuiltInOne()
    {
        // AddTwinCatAdsSimulation shares both helpers, so it inherited the defect too. The rule
        // asserted here is TimeoutMs rather than AmsNetId: this overload's PostConfigure flips
        // every target to Simulated, and a simulated target talks to an in-memory store, so the
        // validator deliberately skips its Net ID. Port and TimeoutMs still apply in every mode.
        var services = new ServiceCollection();

        services.AddSingleton<IValidateOptions<TwinCatAdsOptions>, RejectingValidator>();

        services.AddTwinCatAdsSimulation(o =>
            o.Targets["sim1"] = new PlcTargetOptions { TimeoutMs = 0 });

        using var provider = services.BuildServiceProvider();

        var failures = Failures(provider);

        Assert.Contains(failures.Failures, f => f.Contains("TimeoutMs", StringComparison.Ordinal));
        Assert.Contains(failures.Failures, f => f.Contains(RejectingValidator.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void CalledTwice_RegistersOneBuiltInValidator()
    {
        // TryAddEnumerable dedupes on (ServiceType, ImplementationType), so repeat calls stay
        // idempotent. Without that, a validator registered per call would report every failure
        // twice — one line per registration — in the startup exception.
        var services = new ServiceCollection();

        services.AddTwinCatAds(o =>
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" });
        services.AddTwinCatAds(o =>
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" });

        using var provider = services.BuildServiceProvider();

        Assert.Single(
            provider.GetServices<IValidateOptions<TwinCatAdsOptions>>(),
            v => v is TwinCatAdsOptionsValidator);
    }

    [Fact]
    public void CodeFirstAndConfigurationOverloadsMixed_RegisterOneBuiltInValidator()
    {
        // The two call sites are in different helpers — BindTwinCatAdsOptions and
        // RegisterCodeFirstOptions — and a host composing a config-bound registration with a
        // code-first one runs both. They must still resolve to a single validator instance.
        var services = new ServiceCollection();

        services.AddTwinCatAds(ConfigurationWith("plc1", "1.2.3.4.5.6"));
        services.AddTwinCatAds(o =>
            o.Targets["plc2"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.7" });

        using var provider = services.BuildServiceProvider();

        Assert.Single(
            provider.GetServices<IValidateOptions<TwinCatAdsOptions>>(),
            v => v is TwinCatAdsOptionsValidator);
    }

    /// <summary>A consumer's own validator, which must add to the built-in rules rather than replace them.</summary>
    private sealed class RejectingValidator : IValidateOptions<TwinCatAdsOptions>
    {
        public const string Message = "A rule of the consumer's own.";

        public ValidateOptionsResult Validate(string? name, TwinCatAdsOptions options) =>
            ValidateOptionsResult.Fail(Message);
    }
}
