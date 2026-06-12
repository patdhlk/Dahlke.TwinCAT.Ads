using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Unit tests for <see cref="TwinCatAdsOptionsValidator"/>.
/// All tests hit the validator directly — no hosting, no background services.
/// </summary>
public class TwinCatAdsOptionsValidatorTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static readonly TwinCatAdsOptionsValidator Validator = new();

    private static ValidateOptionsResult Validate(TwinCatAdsOptions options) =>
        Validator.Validate(null, options);

    private static PlcTargetOptions ValidTarget(
        string amsNetId = "1.2.3.4.5.6",
        int port = 851,
        int timeoutMs = 5000) =>
        new() { AmsNetId = amsNetId, Port = port, TimeoutMs = timeoutMs };

    private static TwinCatAdsOptions ValidOptions(string? routerNetId = null) =>
        new()
        {
            Targets = new(StringComparer.OrdinalIgnoreCase)
            {
                ["plc1"] = ValidTarget(),
            },
            Router = new() { NetId = routerNetId },
        };

    // ------------------------------------------------------------------
    // Happy path
    // ------------------------------------------------------------------

    [Fact]
    public void Valid_Config_Passes()
    {
        var result = Validate(ValidOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Valid_Config_With_Router_NetId_Passes()
    {
        var result = Validate(ValidOptions(routerNetId: "192.168.0.1.1.1"));
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Null_Router_NetId_Passes()
    {
        // Null/empty router NetId means "use system router" — always legal.
        var result = Validate(ValidOptions(routerNetId: null));
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Empty_Router_NetId_Passes()
    {
        var result = Validate(ValidOptions(routerNetId: string.Empty));
        Assert.True(result.Succeeded);
    }

    // ------------------------------------------------------------------
    // Targets — empty
    // ------------------------------------------------------------------

    [Fact]
    public void Empty_Targets_Fails_With_Helpful_Message()
    {
        var options = ValidOptions();
        options.Targets.Clear();

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failures);
        var failures = result.Failures!.ToList();
        Assert.Single(failures);

        var msg = failures[0];
        // Message should tell user what configuration key to set.
        Assert.Contains("PlcTargets", msg, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Per-target: AmsNetId
    // ------------------------------------------------------------------

    [Fact]
    public void Target_NullAmsNetId_Fails_Naming_TargetId()
    {
        var options = ValidOptions();
        options.Targets["plc1"] = new PlcTargetOptions { AmsNetId = null!, Port = 851, TimeoutMs = 5000 };

        var result = Validate(options);

        Assert.False(result.Succeeded);
        var failures = result.Failures!.ToList();
        Assert.Single(failures);
        Assert.Contains("plc1", failures[0]);
        Assert.Contains("AmsNetId", failures[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Target_EmptyAmsNetId_Fails_Naming_TargetId()
    {
        var options = ValidOptions();
        options.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "", Port = 851, TimeoutMs = 5000 };

        var result = Validate(options);

        Assert.False(result.Succeeded);
        var failures = result.Failures!.ToList();
        Assert.Single(failures);
        Assert.Contains("plc1", failures[0]);
    }

    [Fact]
    public void Target_WhitespaceAmsNetId_Fails_Naming_TargetId()
    {
        var options = ValidOptions();
        options.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "   ", Port = 851, TimeoutMs = 5000 };

        var result = Validate(options);

        Assert.False(result.Succeeded);
        var failures = result.Failures!.ToList();
        Assert.Single(failures);
        Assert.Contains("plc1", failures[0]);
    }

    [Fact]
    public void Target_InvalidAmsNetId_Fails_With_OffendingValue()
    {
        var options = ValidOptions();
        options.Targets["plc1"] = ValidTarget(amsNetId: "not.an.amsnetid");

        var result = Validate(options);

        Assert.False(result.Succeeded);
        var failures = result.Failures!.ToList();
        Assert.Single(failures);
        // Message must contain the target id and the offending value.
        Assert.Contains("plc1", failures[0]);
        Assert.Contains("not.an.amsnetid", failures[0]);
    }

    // ------------------------------------------------------------------
    // Per-target: Port
    // ------------------------------------------------------------------

    [Fact]
    public void Target_PortZero_Fails_Naming_TargetId_And_Value()
    {
        var options = ValidOptions();
        options.Targets["plc1"] = ValidTarget(port: 0);

        var result = Validate(options);

        Assert.False(result.Succeeded);
        var failures = result.Failures!.ToList();
        Assert.Single(failures);
        Assert.Contains("plc1", failures[0]);
        Assert.Contains("0", failures[0]);
    }

    [Fact]
    public void Target_NegativePort_Fails()
    {
        var options = ValidOptions();
        options.Targets["plc1"] = ValidTarget(port: -1);

        var result = Validate(options);

        Assert.False(result.Succeeded);
        var failures = result.Failures!.ToList();
        Assert.Single(failures);
        Assert.Contains("plc1", failures[0]);
    }

    [Fact]
    public void Target_PortAbove65535_Fails_Naming_TargetId_And_Value()
    {
        var options = ValidOptions();
        options.Targets["plc1"] = ValidTarget(port: 65536);

        var result = Validate(options);

        Assert.False(result.Succeeded);
        var failures = result.Failures!.ToList();
        Assert.Single(failures);
        Assert.Contains("plc1", failures[0]);
        Assert.Contains("65536", failures[0]);
    }

    [Fact]
    public void Target_Port65535_Passes()
    {
        // Base: plc1 with port 851. Change to max valid.
        var options = ValidOptions();
        options.Targets["plc1"] = ValidTarget(port: 65535);
        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void Target_Port1_Passes()
    {
        var options = ValidOptions();
        options.Targets["plc1"] = ValidTarget(port: 1);
        Assert.True(Validate(options).Succeeded);
    }

    // ------------------------------------------------------------------
    // Per-target: TimeoutMs
    // ------------------------------------------------------------------

    [Fact]
    public void Target_TimeoutMs_Zero_Fails_Naming_TargetId_And_Value()
    {
        var options = ValidOptions();
        options.Targets["plc1"] = ValidTarget(timeoutMs: 0);

        var result = Validate(options);

        Assert.False(result.Succeeded);
        var failures = result.Failures!.ToList();
        Assert.Single(failures);
        Assert.Contains("plc1", failures[0]);
        Assert.Contains("0", failures[0]);
    }

    [Fact]
    public void Target_TimeoutMs_Negative_Fails()
    {
        var options = ValidOptions();
        options.Targets["plc1"] = ValidTarget(timeoutMs: -100);

        var result = Validate(options);

        Assert.False(result.Succeeded);
        var failures = result.Failures!.ToList();
        Assert.Single(failures);
        Assert.Contains("plc1", failures[0]);
    }

    // ------------------------------------------------------------------
    // Multiple violations — all reported
    // ------------------------------------------------------------------

    [Fact]
    public void Multiple_Violations_All_Reported_In_Single_Result()
    {
        var options = new TwinCatAdsOptions
        {
            Targets = new(StringComparer.OrdinalIgnoreCase)
            {
                // target1: bad AmsNetId
                ["target1"] = new PlcTargetOptions { AmsNetId = "bad-id", Port = 851, TimeoutMs = 5000 },
                // target2: bad port and bad timeout
                ["target2"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6", Port = 0, TimeoutMs = -1 },
            },
            Router = new() { NetId = "also-bad-router-id" },
        };

        var result = Validate(options);

        Assert.False(result.Succeeded);
        var failures = result.Failures!.ToList();

        // At minimum: bad AmsNetId for target1, bad port for target2,
        // bad timeout for target2, bad Router.NetId = 4 failures.
        Assert.True(failures.Count >= 4,
            $"Expected ≥4 failures but got {failures.Count}: {string.Join("; ", failures)}");
    }

    [Fact]
    public void Multiple_Targets_Single_Bad_Reports_Correct_TargetId()
    {
        var options = new TwinCatAdsOptions
        {
            Targets = new(StringComparer.OrdinalIgnoreCase)
            {
                ["good"]  = ValidTarget(),
                ["bad"]   = ValidTarget(amsNetId: "not-valid"),
            },
        };

        var result = Validate(options);

        Assert.False(result.Succeeded);
        var failures = result.Failures!.ToList();
        Assert.Single(failures);
        Assert.Contains("bad", failures[0]);
        Assert.DoesNotContain("good", failures[0]);
    }

    // ------------------------------------------------------------------
    // Router.NetId
    // ------------------------------------------------------------------

    [Fact]
    public void Router_InvalidNetId_Fails_With_OffendingValue()
    {
        var options = ValidOptions(routerNetId: "totally.invalid");

        var result = Validate(options);

        Assert.False(result.Succeeded);
        var failures = result.Failures!.ToList();
        Assert.Single(failures);
        Assert.Contains("totally.invalid", failures[0]);
    }

    [Fact]
    public void Router_ValidNetId_Passes()
    {
        var result = Validate(ValidOptions(routerNetId: "10.0.0.1.1.1"));
        Assert.True(result.Succeeded);
    }

    // ------------------------------------------------------------------
    // Diagnostics.SymbolDump.MaxDepth
    // ------------------------------------------------------------------

    [Fact]
    public void SymbolDump_NegativeMaxDepth_Fails()
    {
        var options = ValidOptions();
        options.Diagnostics.SymbolDump.MaxDepth = -1;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        var failures = result.Failures!.ToList();
        Assert.Single(failures);
        Assert.Contains("-1", failures[0]);
    }

    [Fact]
    public void SymbolDump_ZeroMaxDepth_Passes()
    {
        var options = ValidOptions();
        options.Diagnostics.SymbolDump.MaxDepth = 0;

        var result = Validate(options);

        Assert.True(result.Succeeded);
    }

    // ------------------------------------------------------------------
    // Integration-style: ValidateOnStart wiring
    // ------------------------------------------------------------------

    [Fact]
    public void ValidateOnStart_AddTwinCatAds_InvalidConfig_ThrowsOptionsValidationException()
    {
        // Arrange: empty config → no PlcTargets → validation must fail.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddTwinCatAds(config);
        using var sp = services.BuildServiceProvider();

        // Resolving .Value always invokes IValidateOptions — this is the standard
        // way to prove validation is wired up without requiring a hosted app.
        var options = sp.GetRequiredService<IOptions<TwinCatAdsOptions>>();
        Assert.Throws<OptionsValidationException>(() => _ = options.Value);
    }

    [Fact]
    public void ValidateOnStart_AddTwinCatAdsSimulation_InvalidConfig_ThrowsOptionsValidationException()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddTwinCatAdsSimulation(config);
        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<TwinCatAdsOptions>>();
        Assert.Throws<OptionsValidationException>(() => _ = options.Value);
    }

    [Fact]
    public void ValidateOnStart_AddTwinCatAds_ValidConfig_DoesNotThrow()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlcTargets:plc1:AmsNetId"]  = "1.2.3.4.5.6",
                ["PlcTargets:plc1:Port"]      = "851",
                ["PlcTargets:plc1:TimeoutMs"] = "5000",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddTwinCatAds(config);
        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<TwinCatAdsOptions>>();
        var value = options.Value; // Should not throw.
        Assert.Single(value.Targets);
    }

    [Fact]
    public async Task ValidateOnStart_InvalidConfig_ThrowsDuringHostStart()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
                services.AddTwinCatAds(new ConfigurationBuilder().Build()))
            .Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }
}
