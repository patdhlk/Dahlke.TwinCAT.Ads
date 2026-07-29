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
        ValidateRawChannels(options, failures);

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
            ValidateTargetSymbolBrowseTimeout(targetId, target, failures);
            ValidateTargetInitialValues(target, failures);
        }
    }

    /// <summary>
    /// Surfaces problems <see cref="InitialValueBinder"/> found while re-binding config-declared
    /// seed values. The messages are already target-scoped and actionable, so they are relayed
    /// verbatim.
    /// </summary>
    private static void ValidateTargetInitialValues(
        PlcTargetOptions target,
        List<string> failures)
        => failures.AddRange(target.InitialValueBindingErrors);

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

    /// <summary>
    /// Was previously unvalidated because <see cref="AdsClient.Timeout"/> was
    /// never wired to it — Beckhoff's invisible 5000 ms default made a bad value
    /// inert. It now flows into <c>Task.Delay(SymbolBrowseTimeoutMs, ...)</c> on
    /// the first symbol browse, and <see cref="Task.Delay(int,CancellationToken)"/>
    /// throws <see cref="ArgumentOutOfRangeException"/> for any negative value
    /// other than <c>-1</c>. Catching a bad value here, at startup, is the same
    /// standard this validator already applies to raw-channel seed entries: a typo
    /// fails the host instead of a poll hours later.
    /// </summary>
    private static void ValidateTargetSymbolBrowseTimeout(
        string targetId,
        PlcTargetOptions target,
        List<string> failures)
    {
        if (target.SymbolBrowseTimeoutMs <= 0)
        {
            failures.Add(
                $"Target '{targetId}': SymbolBrowseTimeoutMs '{target.SymbolBrowseTimeoutMs}' must be greater than zero. " +
                $"Fix 'PlcTargets:{targetId}:SymbolBrowseTimeoutMs' (default: 30000 ms).");
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

    // ------------------------------------------------------------------
    // RawChannels
    // ------------------------------------------------------------------

    /// <summary>
    /// Validates <see cref="AdsRawChannelOptions"/>. Seed entries are parsed HERE,
    /// at startup, so a malformed AMS Net ID or index group fails the host rather
    /// than a poll hours later.
    /// </summary>
    private static void ValidateRawChannels(TwinCatAdsOptions options, List<string> failures)
    {
        var raw = options.RawChannels;

        if (raw.TimeoutMs <= 0)
            failures.Add($"RawChannels:TimeoutMs must be greater than 0 (was {raw.TimeoutMs}).");

        if (raw.RetryCount < 0)
            failures.Add($"RawChannels:RetryCount must not be negative (was {raw.RetryCount}).");

        if (raw.IdleEvictionMs <= 0)
            failures.Add($"RawChannels:IdleEvictionMs must be greater than 0 (was {raw.IdleEvictionMs}).");

        // Entries the binder threw away. Relayed verbatim: the message is already
        // path-scoped and actionable, exactly like InitialValueBindingErrors.
        failures.AddRange(raw.SeedBindingErrors);

        for (var i = 0; i < raw.Seed.Count; i++)
            ValidateRawSeed(raw.Seed[i], i, failures);
    }

    /// <summary>
    /// Validates one <see cref="AdsRawChannelSeed"/>. Messages carry BOTH the list
    /// index — the configuration path an operator edits is
    /// <c>RawChannels:Seed:{index}:…</c> — and the offending value, because an
    /// index alone is unsearchable and a value alone is ambiguous across entries.
    /// </summary>
    private static void ValidateRawSeed(
        AdsRawChannelSeed seed,
        int index,
        List<string> failures)
    {
        if (!RawSeedParser.IsWellFormedNetId(seed.AmsNetId))
        {
            failures.Add(
                $"RawChannels:Seed:{index}:AmsNetId '{seed.AmsNetId}' is not six dot-separated " +
                $"octets in the range 0-255 (e.g. '192.168.1.10.3.1').");
        }

        if (seed.Port is < 0 or > 65535)
        {
            failures.Add(
                $"RawChannels:Seed:{index}:Port '{seed.Port}' is outside the range 0-65535.");
        }

        for (var s = 0; s < seed.Slots.Count; s++)
        {
            var slot = seed.Slots[s];

            if (!RawSeedParser.TryParseIndex(slot.IndexGroup, out _))
            {
                failures.Add(
                    $"RawChannels:Seed:{index}:Slots:{s}:IndexGroup '{slot.IndexGroup}' is not a " +
                    $"number (decimal or 0x-prefixed hex, no sign, no whitespace).");
            }

            if (!RawSeedParser.TryParseIndex(slot.IndexOffset, out _))
            {
                failures.Add(
                    $"RawChannels:Seed:{index}:Slots:{s}:IndexOffset '{slot.IndexOffset}' is not a " +
                    $"number (decimal or 0x-prefixed hex, no sign, no whitespace).");
            }

            if (!RawSeedParser.TryParseHex(slot.Bytes, out _, out var payloadError))
                failures.Add($"RawChannels:Seed:{index}:Slots:{s}:Bytes — {payloadError}");
        }
    }
}
