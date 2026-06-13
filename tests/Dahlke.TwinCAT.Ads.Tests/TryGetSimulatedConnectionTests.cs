using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Tests for the deliberate test-support escape hatch
/// <see cref="IAdsConnectionPool.TryGetSimulatedConnection"/>. With facades
/// wrapping every connection, test code cannot reach the
/// <see cref="SimulatedAdsConnection"/> behind a facade for seeding/inspection;
/// this API exposes it, but only for live simulated targets, and never throws.
/// </summary>
public class TryGetSimulatedConnectionTests
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Builds a pool over the REAL <see cref="AdsConnectionFactory"/> so that a
    /// simulated target actually publishes a <see cref="SimulatedAdsConnection"/>
    /// (the fake factory used elsewhere yields fakes, not the real sim type).
    /// </summary>
    private static AdsConnectionPool CreatePool(params (string Id, ConnectionMode Mode)[] targets)
    {
        var dict = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, mode) in targets)
            dict[id] = new PlcTargetOptions { DisplayName = id, Mode = mode, AmsNetId = "1.2.3.4.5.6" };

        var options = Options.Create(new TwinCatAdsOptions { Targets = dict });
        return new AdsConnectionPool(
            options,
            new AdsConnectionFactory(NullLoggerFactory.Instance),
            new AdsRouterReadySignal(),
            NullLoggerFactory.Instance,
            new FakeTimeProvider());
    }

    /// <summary>Polls until the facade for <paramref name="plcId"/> has published a connection.</summary>
    private static async Task WaitForConnection(AdsConnectionPool pool, string plcId)
    {
        var deadline = DateTime.UtcNow + RealTimeout;
        while (((AdsConnectionFacade)pool.GetConnection(plcId)).CurrentForTesting is null)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Facade for '{plcId}' never published a connection.");
            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }
    }

    [Fact]
    public async Task SimulatedTarget_ReturnsTrueAndInstance_OnceStarted()
    {
        var pool = CreatePool(("sim", ConnectionMode.Simulated));
        await pool.StartAsync(CancellationToken.None);
        try
        {
            // Simulated loops start immediately (no router wait) and publish a
            // SimulatedAdsConnection into the facade.
            await WaitForConnection(pool, "sim");

            var ok = pool.TryGetSimulatedConnection("sim", out var sim);

            Assert.True(ok);
            Assert.NotNull(sim);
            // The returned instance is the very one the facade routes to — so
            // seeding it is observable through the facade.
            Assert.Same(((AdsConnectionFacade)pool.GetConnection("sim")).CurrentForTesting, sim);

            // Case-insensitive id lookup, matching GetConnection's contract.
            Assert.True(pool.TryGetSimulatedConnection("SIM", out var simUpper));
            Assert.Same(sim, simUpper);
        }
        finally
        {
            await pool.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RealTarget_ReturnsFalse()
    {
        // A real target's loop is deferred until the router signals ready; we never
        // signal it, so no connection is ever published. The escape hatch must
        // report false (it is not a simulated connection) without throwing.
        var pool = CreatePool(("real", ConnectionMode.Real));
        await pool.StartAsync(CancellationToken.None);
        try
        {
            var ok = pool.TryGetSimulatedConnection("real", out var sim);

            Assert.False(ok);
            Assert.Null(sim);
        }
        finally
        {
            await pool.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void UnknownId_ReturnsFalse_WithoutThrowing()
    {
        var pool = CreatePool(("sim", ConnectionMode.Simulated));

        // No StartAsync, unknown id: still no throw, just false.
        var ok = pool.TryGetSimulatedConnection("does-not-exist", out var sim);

        Assert.False(ok);
        Assert.Null(sim);
    }

    [Fact]
    public void SimulatedTarget_BeforeStart_ReturnsFalse()
    {
        // Configured simulated target but the pool has not been started, so its
        // loop has not published a connection yet — the hatch returns false.
        var pool = CreatePool(("sim", ConnectionMode.Simulated));

        var ok = pool.TryGetSimulatedConnection("sim", out var sim);

        Assert.False(ok);
        Assert.Null(sim);
    }
}
