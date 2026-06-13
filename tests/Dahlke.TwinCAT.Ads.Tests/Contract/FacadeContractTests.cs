using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests.Contract;

/// <summary>
/// Runs the shared <see cref="AdsConnectionContractTests"/> against an
/// <see cref="AdsConnectionFacade"/> routing to an independent store-backed
/// <see cref="InMemoryManagedConnection"/> — the implementation a consumer holds when using the
/// pool (the facade is the public <see cref="IAdsConnection"/>; the managed connection it wraps
/// is never exposed).
/// </summary>
/// <remarks>
/// <para>
/// The in-memory connection is published into the facade via
/// <see cref="AdsConnectionFacade.SetCurrent"/> at harness construction so every operation takes
/// the facade's fast path (a connection is already current). The backdoor writes through the
/// SAME in-memory connection's store, so on-change subscription firing reaches the facade's
/// durable subscription registrations exactly as a real notification would.
/// </para>
/// <para>
/// Inherits every contract [Fact] with NO overrides — same drift-detection contract as the
/// simulated suite. A fact this implementation cannot satisfy is a FINDING, not a carve-out.
/// </para>
/// </remarks>
public sealed class FacadeContractTests : AdsConnectionContractTests
{
    protected override Task<ContractHarness> CreateHarnessAsync()
    {
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1",
            new PlcTargetOptions { DisplayName = "PLC One", TimeoutMs = 5000 },
            time,
            NullLogger.Instance);

        var inner = new InMemoryManagedConnection("plc1", "PLC One") { IsConnected = true };
        facade.SetCurrent(inner);

        // Backdoor: write straight into the underlying in-memory store (firing its on-change
        // subscribers), so the facade's durable subscription registrations — which the facade
        // registered against THIS connection via SetCurrent — receive the notification.
        Func<string, object?, Task> writeRaw = (path, value) =>
            inner.WriteValueAsync(path, value!, CancellationToken.None);

        Func<ValueTask> dispose = () =>
        {
            inner.Dispose();
            return ValueTask.CompletedTask;
        };

        return Task.FromResult(new ContractHarness(facade, writeRaw, dispose));
    }
}
