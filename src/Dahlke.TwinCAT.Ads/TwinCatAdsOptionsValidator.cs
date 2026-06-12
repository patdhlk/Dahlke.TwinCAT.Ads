using Microsoft.Extensions.Options;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Validates <see cref="TwinCatAdsOptions"/> at application startup.
/// All failures are collected into a single <see cref="ValidateOptionsResult"/>
/// so the operator sees every misconfiguration at once rather than fixing
/// problems one by one.
/// </summary>
internal sealed class TwinCatAdsOptionsValidator : IValidateOptions<TwinCatAdsOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TwinCatAdsOptions options)
    {
        var failures = new List<string>();

        ValidateTargets(options, failures);
        ValidateRouter(options, failures);
        ValidateDiagnostics(options, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    // ------------------------------------------------------------------
    // Targets
    // ------------------------------------------------------------------

    private static void ValidateTargets(TwinCatAdsOptions options, List<string> failures)
    {
        if (options.Targets is null || options.Targets.Count == 0)
        {
            failures.Add(
                "At least one PLC target must be configured. " +
                "Add targets under the 'PlcTargets' configuration section " +
                "(e.g. PlcTargets:myPlc:AmsNetId = '1.2.3.4.5.6') " +
                "or register targets via code-first configuration.");
            return;
        }

        foreach (var (targetId, target) in options.Targets)
        {
            // Simulated targets talk to an in-memory store, not AMS/ADS, so they
            // need no AMS Net ID — skip that check. Port and TimeoutMs checks
            // still apply for consistency across modes.
            if (target.Mode == ConnectionMode.Real)
                ValidateTargetAmsNetId(targetId, target, failures);

            ValidateTargetPort(targetId, target, failures);
            ValidateTargetTimeout(targetId, target, failures);
        }
    }

    private static void ValidateTargetAmsNetId(
        string targetId,
        PlcTargetOptions target,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(target.AmsNetId))
        {
            failures.Add(
                $"Target '{targetId}': AmsNetId is required. " +
                $"Set 'PlcTargets:{targetId}:AmsNetId' to a valid AMS Net ID (e.g. '1.2.3.4.5.6').");
            return;
        }

        if (!AmsNetId.TryParse(target.AmsNetId, out _))
        {
            failures.Add(
                $"Target '{targetId}': AmsNetId '{target.AmsNetId}' is not a valid AMS Net ID. " +
                $"Expected six dot-separated octets, e.g. '192.168.1.10.1.1'. " +
                $"Fix 'PlcTargets:{targetId}:AmsNetId'.");
        }
    }

    private static void ValidateTargetPort(
        string targetId,
        PlcTargetOptions target,
        List<string> failures)
    {
        if (target.Port <= 0 || target.Port > 65535)
        {
            failures.Add(
                $"Target '{targetId}': Port '{target.Port}' is outside the valid range [1, 65535]. " +
                $"Fix 'PlcTargets:{targetId}:Port' (typical TwinCAT 3 value: 851).");
        }
    }

    private static void ValidateTargetTimeout(
        string targetId,
        PlcTargetOptions target,
        List<string> failures)
    {
        if (target.TimeoutMs <= 0)
        {
            failures.Add(
                $"Target '{targetId}': TimeoutMs '{target.TimeoutMs}' must be greater than zero. " +
                $"Fix 'PlcTargets:{targetId}:TimeoutMs' (default: 5000 ms).");
        }
    }

    // ------------------------------------------------------------------
    // Router
    // ------------------------------------------------------------------

    private static void ValidateRouter(TwinCatAdsOptions options, List<string> failures)
    {
        var netId = options.Router?.NetId;

        // Null or empty means "use system router" — always valid.
        if (string.IsNullOrEmpty(netId))
            return;

        if (!AmsNetId.TryParse(netId, out _))
        {
            failures.Add(
                $"Router.NetId '{netId}' is not a valid AMS Net ID. " +
                $"Expected six dot-separated octets, e.g. '127.0.0.1.1.1'. " +
                $"Fix 'AmsRouter:NetId', or remove the key to disable the embedded router.");
        }
    }

    // ------------------------------------------------------------------
    // Diagnostics
    // ------------------------------------------------------------------

    private static void ValidateDiagnostics(TwinCatAdsOptions options, List<string> failures)
    {
        var maxDepth = options.Diagnostics?.SymbolDump?.MaxDepth ?? 0;

        if (maxDepth < 0)
        {
            failures.Add(
                $"Diagnostics.SymbolDump.MaxDepth '{maxDepth}' must be ≥ 0. " +
                $"Fix 'AdsSymbolDump:MaxDepth' (default: 1; use 0 to traverse all levels).");
        }
    }
}
