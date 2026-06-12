using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Proves that a <see cref="ConnectionMode.Simulated"/> target lives happily
/// inside the REAL <see cref="AdsConnectionPool"/> with no special-casing in the
/// pool itself: the loop calls Connect() (sim no-op succeeds instantly), stores
/// the connection, then health-checks IsAliveAsync (sim always true) — so a
/// simulated target is connected immediately and never reconnect-churns.
///
/// Two factory variants are exercised:
///   - a counting fake factory wrapping real <see cref="SimulatedAdsConnection"/>
///     instances, to assert CreateCount stays 1 across many health intervals;
///   - the REAL <see cref="AdsConnectionFactory"/>, end-to-end, to prove
///     dispatch + seeding without hardware.
/// </summary>
public class AdsConnectionPoolSimulatedTests
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Health = TimeSpan.FromSeconds(5);

    // Since the C11 facade redesign, GetConnection returns the stable facade
    // eagerly (before the underlying connects), so waiting for non-null no longer
    // means "connected". Wait until the facade reports IsConnected — i.e. the
    // underlying connection has been published into it.
    private static async Task WaitForConnection(AdsConnectionPool pool, string plcId)
    {
        var deadline = DateTime.UtcNow + RealTimeout;
        while (pool.GetConnection(plcId) is not { IsConnected: true })
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Facade for '{plcId}' never became connected.");
            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }
    }

    /// <summary>
    /// Counting factory that always produces a real <see cref="SimulatedAdsConnection"/>.
    /// Lets the test assert how many times the pool rebuilt the connection.
    /// </summary>
    private sealed class CountingSimFactory : IAdsConnectionFactory
    {
        public int CreateCount;
        public IManagedConnection? Last;

        public IManagedConnection Create(string plcId, PlcTargetOptions options)
        {
            Interlocked.Increment(ref CreateCount);
            var connection = new SimulatedAdsConnection(plcId, options.DisplayName, NullLoggerFactory.Instance);
            connection.SetInitialValues(options.InitialValues);
            Last = connection;
            return connection;
        }
    }

    private static AdsConnectionPool CreatePool(
        IAdsConnectionFactory factory,
        FakeTimeProvider time,
        AdsRouterReadySignal signal,
        params (string id, PlcTargetOptions opts)[] targets)
    {
        var dict = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, opts) in targets)
            dict[id] = opts;

        var adsOptions = new TwinCatAdsOptions { Targets = dict };

        return new AdsConnectionPool(
            Options.Create(adsOptions),
            factory,
            signal,
            NullLogger<AdsConnectionPool>.Instance,
            time);
    }

    [Fact]
    public async Task SimulatedTarget_ConnectsOnce_AndNeverChurns_AcrossManyHealthIntervals()
    {
        var factory = new CountingSimFactory();
        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal();
        var pool = CreatePool(
            factory, time, signal,
            ("sim1", new PlcTargetOptions { Mode = ConnectionMode.Simulated, DisplayName = "Sim" }));

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await WaitForConnection(pool, "sim1");
        var first = pool.GetConnection("sim1");
        Assert.NotNull(first);
        Assert.True(first!.IsConnected);
        Assert.Equal(1, factory.CreateCount);

        // Advance several health-check intervals. IsAliveAsync is always true for
        // a simulated connection, so the inner health loop never breaks and the
        // pool never rebuilds: CreateCount stays 1, same instance throughout.
        for (var i = 0; i < 10; i++)
        {
            time.Advance(Health);
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        Assert.Equal(1, factory.CreateCount);
        Assert.Same(first, pool.GetConnection("sim1"));
        Assert.True(pool.GetConnection("sim1")!.IsConnected);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task ForceReconnect_SimulatedTarget_IsNoOp_RetainsSameInstanceAndWrittenValues()
    {
        // Arrange: pool with real factory and a simulated target
        var factory = new AdsConnectionFactory(NullLoggerFactory.Instance);
        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal();
        var pool = CreatePool(
            factory, time, signal,
            ("sim1", new PlcTargetOptions
            {
                Mode = ConnectionMode.Simulated,
                DisplayName = "Sim",
                InitialValues = new() { ["MAIN.nSpeed"] = 100 },
            }));

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);
        await WaitForConnection(pool, "sim1");

        var connectionBefore = pool.GetConnection("sim1");
        Assert.NotNull(connectionBefore);

        // Write a runtime value (not in InitialValues)
        await connectionBefore!.WriteValueAsync("MAIN.bEnabled", true, CancellationToken.None);

        // Act: ForceReconnect on a simulated target should be a no-op
        pool.ForceReconnect("sim1");

        // Give the pool a moment — ensure no async loop restarts
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        // Assert: same connection instance returned
        var connectionAfter = pool.GetConnection("sim1");
        Assert.Same(connectionBefore, connectionAfter);

        // Assert: runtime-written value still readable (instance not replaced)
        var value = await connectionAfter!.ReadValueAsync("MAIN.bEnabled", CancellationToken.None);
        Assert.Equal(true, value);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task RealFactory_SimulatedTarget_EndToEnd_ConnectsAndReadsSeededValue()
    {
        // Uses the REAL AdsConnectionFactory (not a fake) to prove the dispatch
        // path: Simulated mode -> SimulatedAdsConnection seeded with InitialValues.
        var factory = new AdsConnectionFactory(NullLoggerFactory.Instance);
        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal();
        var pool = CreatePool(
            factory, time, signal,
            ("sim1", new PlcTargetOptions
            {
                Mode = ConnectionMode.Simulated,
                DisplayName = "Sim",
                InitialValues = new() { ["MAIN.nSpeed"] = 1500 },
            }));

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await WaitForConnection(pool, "sim1");
        var connection = pool.GetConnection("sim1");
        Assert.NotNull(connection);
        Assert.True(connection!.IsConnected);

        var value = await connection.ReadValueAsync("MAIN.nSpeed", CancellationToken.None);
        Assert.Equal(1500, value);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }
}
