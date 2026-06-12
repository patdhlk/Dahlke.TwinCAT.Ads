using Microsoft.Extensions.Logging.Abstractions;

namespace Dahlke.TwinCAT.Ads.Tests.Contract;

/// <summary>
/// Runs the shared <see cref="AdsConnectionContractTests"/> against a
/// <see cref="SimulatedAdsConnection"/> exercised DIRECTLY (no facade) — the implementation a
/// consumer holds when using the sim in tests or offline development.
/// </summary>
/// <remarks>
/// Inherits every contract [Fact] with NO overrides. If a contract fact cannot be satisfied by
/// this implementation, that is a behavioural FINDING to surface — not a per-class carve-out.
/// </remarks>
public sealed class SimulatedAdsConnectionContractTests : AdsConnectionContractTests
{
    protected override Task<ContractHarness> CreateHarnessAsync()
    {
        var sim = new SimulatedAdsConnection("test-plc", "Test PLC", NullLoggerFactory.Instance);

        // Backdoor: a direct on-change write into the sim's store — the same path a [Fact]
        // would use to arrange a notification, so seeding and notification share one mechanism.
        Func<string, object?, Task> writeRaw = (path, value) =>
            sim.WriteValueAsync(path, value!, CancellationToken.None);

        Func<ValueTask> dispose = () =>
        {
            sim.Dispose();
            return ValueTask.CompletedTask;
        };

        return Task.FromResult(new ContractHarness(sim, writeRaw, dispose));
    }
}
