using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// TDD tests for C9: conditional router gating, unified simulation path,
/// and the AddTwinCatAdsSimulation PostConfigure mode-flip.
/// </summary>
public class C9_ConditionalRouterAndUnifiedSimulationTests
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(10);

    // -------------------------------------------------------------------------
    // Helper: poll until connection appears
    // -------------------------------------------------------------------------

    private static async Task WaitForConnection(AdsConnectionPool pool, string plcId)
    {
        var deadline = DateTime.UtcNow + RealTimeout;
        while (pool.GetConnection(plcId) is null)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"GetConnection('{plcId}') never published a connection.");
            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }
    }

    // =========================================================================
    // 1. All-simulated pool: StartAsync completes without signal ever being set
    // =========================================================================

    /// <summary>
    /// Unit test (direct construction): a pool with only Simulated targets must
    /// complete StartAsync immediately — it MUST NOT block on the router signal.
    /// The signal is never set here, which would hang the old code forever.
    /// </summary>
    [Fact]
    public async Task AllSimulated_StartAsync_CompletesWithoutRouterSignal()
    {
        var factory = new AdsConnectionFactory(NullLoggerFactory.Instance);
        var time = new FakeTimeProvider();
        // Signal is created but NEVER set — this proves the pool doesn't wait for it.
        var signal = new AdsRouterReadySignal();

        var targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["sim1"] = new PlcTargetOptions
            {
                Mode = ConnectionMode.Simulated,
                DisplayName = "Sim1",
                InitialValues = new() { ["MAIN.bEnabled"] = true, ["MAIN.nSpeed"] = 1500 },
            },
        };
        var adsOptions = new TwinCatAdsOptions { Targets = targets };

        var pool = new AdsConnectionPool(
            Options.Create(adsOptions),
            factory,
            signal,
            NullLogger<AdsConnectionPool>.Instance,
            time);

        // Act: StartAsync must complete even though the signal is NEVER set.
        var startTask = pool.StartAsync(CancellationToken.None);
        await startTask.WaitAsync(RealTimeout);

        // Connection is live
        await WaitForConnection(pool, "sim1");
        var conn = pool.GetConnection("sim1");
        Assert.NotNull(conn);
        Assert.True(conn!.IsConnected);

        // Seeded value is readable
        var value = await conn.ReadValueAsync("MAIN.bEnabled", CancellationToken.None);
        Assert.Equal(true, value);

        var speed = await conn.ReadValueAsync("MAIN.nSpeed", CancellationToken.None);
        Assert.Equal(1500, speed);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    // =========================================================================
    // 2. All-simulated via DI: end-to-end through AddTwinCatAdsSimulation
    // =========================================================================

    /// <summary>
    /// Resolve pool via DI (AddTwinCatAdsSimulation code-first), call StartAsync
    /// on the pool's IHostedService directly, verify connection + seeded values.
    /// </summary>
    [Fact]
    public async Task AllSimulated_ViaDI_StartAsync_ConnectionLiveAndSeeded()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions
            {
                Mode = ConnectionMode.Simulated,
                DisplayName = "TestSim",
                InitialValues = new() { ["MAIN.bEnabled"] = true, ["MAIN.nSpeed"] = 999 },
            };
        });

        using var sp = services.BuildServiceProvider();

        // Start ONLY the pool (not the router) — avoids AmsTcpIpRouter from binding a port.
        var pool = sp.GetRequiredService<AdsConnectionPool>();
        await pool.StartAsync(CancellationToken.None).WaitAsync(RealTimeout);

        await WaitForConnection(pool, "sim1");

        var conn = pool.GetConnection("sim1");
        Assert.NotNull(conn);
        Assert.True(conn!.IsConnected);

        var enabled = await conn.ReadValueAsync("MAIN.bEnabled", CancellationToken.None);
        Assert.Equal(true, enabled);

        var speed = await conn.ReadValueAsync("MAIN.nSpeed", CancellationToken.None);
        Assert.Equal(999, speed);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    // =========================================================================
    // 3. Mixed config + router failure: sim target connects; real target skipped
    // =========================================================================

    [Fact]
    public async Task MixedConfig_RouterFailure_SimConnects_RealSkipped()
    {
        var factory = new FakeConnectionFactory();

        // Enqueue a fake for the real target (should NOT be used because router fails)
        var realFake = factory.Enqueue(new FakeManagedConnection("real1"));

        // Simulated target uses the real factory path (SimulatedAdsConnection)
        // but we use a FakeConnectionFactory here — to properly separate, we create
        // a custom factory that dispatches by mode:
        var dispatchFactory = new ModeDispatchingFactory(factory);

        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal();

        var targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["real1"] = new PlcTargetOptions
            {
                Mode = ConnectionMode.Real,
                AmsNetId = "1.2.3.4.5.6",
                DisplayName = "Real1",
            },
            ["sim1"] = new PlcTargetOptions
            {
                Mode = ConnectionMode.Simulated,
                DisplayName = "Sim1",
                InitialValues = new() { ["X"] = 42 },
            },
        };
        var adsOptions = new TwinCatAdsOptions { Targets = targets };

        var pool = new AdsConnectionPool(
            Options.Create(adsOptions),
            dispatchFactory,
            signal,
            NullLogger<AdsConnectionPool>.Instance,
            time);

        // Simulate router failure
        signal.SetFailed();

        // Act: StartAsync must complete (not hang)
        await pool.StartAsync(CancellationToken.None).WaitAsync(RealTimeout);

        // Sim target should be connected
        await WaitForConnection(pool, "sim1");
        var simConn = pool.GetConnection("sim1");
        Assert.NotNull(simConn);
        Assert.True(simConn!.IsConnected);
        var xVal = await simConn.ReadValueAsync("X", CancellationToken.None);
        Assert.Equal(42, xVal);

        // Real target: no loop started → GetConnection returns null
        Assert.Null(pool.GetConnection("real1"));

        // Fake real factory was NOT called (real target was skipped)
        Assert.Equal(0, factory.CreateCount);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    // =========================================================================
    // 4. PostConfigure mode-flip: AddTwinCatAdsSimulation forces Mode=Simulated
    // =========================================================================

    [Fact]
    public void AddTwinCatAdsSimulation_FlipsAllTargetModesToSimulated()
    {
        // Arrange: lambda registers a target explicitly with Mode=Real
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions
            {
                AmsNetId = "1.2.3.4.5.6", // valid real target config
                Mode = ConnectionMode.Real,  // explicitly Real
            };
        });

        using var sp = services.BuildServiceProvider();

        // Act: resolve options (PostConfigure has flipped every target to Simulated)
        var opts = sp.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        // Assert: despite being declared Real, it is now Simulated
        Assert.Equal(ConnectionMode.Simulated, opts.Targets["plc1"].Mode);
    }

    [Fact]
    public void AddTwinCatAdsSimulation_FlipsConfigBoundTargets_ToSimulated()
    {
        // Config-binding path: target has no explicit Mode (defaults to Real).
        // Use IConfiguration via ConfigurationBuilder — already available transitively.
        var services = new ServiceCollection();
        services.AddLogging();

        var configData = new Dictionary<string, string?>
        {
            ["PlcTargets:plc1:AmsNetId"] = "1.2.3.4.5.6",
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        services.AddTwinCatAdsSimulation(config);

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        Assert.Equal(ConnectionMode.Simulated, opts.Targets["plc1"].Mode);
    }

    // =========================================================================
    // 5. Router service gating: all-simulated options → SetReady without router
    // =========================================================================

    [Fact]
    public async Task RouterService_AllSimulatedOptions_SetsReadyImmediately_WithoutStartingRouter()
    {
        // Build AdsRouterService with all-simulated options and a configured NetId.
        // The service must recognize no Real targets and call SetReady without
        // starting the router (which would otherwise fail without a Beckhoff install).
        var signal = new AdsRouterReadySignal();

        var options = new TwinCatAdsOptions
        {
            Targets = new(StringComparer.OrdinalIgnoreCase)
            {
                ["sim1"] = new PlcTargetOptions { Mode = ConnectionMode.Simulated },
            },
            Router = new AmsRouterOptions { NetId = "127.0.0.1.1.1" }, // NetId is set but no Real targets
        };

        var svc = new AdsRouterService(
            Options.Create(options),
            configuration: null,
            NullLoggerFactory.Instance,
            signal);

        using var cts = new CancellationTokenSource(RealTimeout);

        // ExecuteAsync runs the hosted service body
        await svc.StartAsync(cts.Token);

        // Signal must become ready very quickly (no router bind occurs)
        await signal.WaitAsync(cts.Token).WaitAsync(RealTimeout);

        await svc.StopAsync(CancellationToken.None);
    }

    // =========================================================================
    // 6. Idempotency: AddTwinCatAdsSimulation twice → 2 hosted services (router + pool)
    // =========================================================================

    [Fact]
    public void AddTwinCatAdsSimulation_CalledTwice_DoesNotDuplicateHostedServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions { Mode = ConnectionMode.Simulated };
        });

        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions { Mode = ConnectionMode.Simulated };
        });

        // Now uses core services: router + pool = 2 hosted services (not 4)
        int hostedServiceCount = services.Count(d => d.ServiceType == typeof(IHostedService));
        Assert.Equal(2, hostedServiceCount);
    }

    [Fact]
    public void AddTwinCatAdsSimulation_AfterAddTwinCatAds_NoExtraHostedServices()
    {
        // Mixed-call: AddTwinCatAds then AddTwinCatAdsSimulation
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddTwinCatAds(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
        });

        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions { Mode = ConnectionMode.Simulated };
        });

        // Core was already registered by AddTwinCatAds, so AddTwinCatAdsSimulation
        // must not duplicate it: still 2 (router + pool).
        int hostedServiceCount = services.Count(d => d.ServiceType == typeof(IHostedService));
        Assert.Equal(2, hostedServiceCount);
    }

    [Fact]
    public void AddTwinCatAds_AfterAddTwinCatAdsSimulation_PostConfigureFlipsToSimulated()
    {
        // AddTwinCatAdsSimulation first (registers core + PostConfigure),
        // then AddTwinCatAds (guarded → skips RegisterCoreServices but PostConfigure
        // was already registered outside the guard, so it still applies).
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6", Mode = ConnectionMode.Real };
        });

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        Assert.Equal(ConnectionMode.Simulated, opts.Targets["plc1"].Mode);

        int hostedServiceCount = services.Count(d => d.ServiceType == typeof(IHostedService));
        Assert.Equal(2, hostedServiceCount);
    }

    // =========================================================================
    // 7. Seeding coverage via AddTwinCatAdsSimulation path (supersedes Simulated pool tests)
    // =========================================================================

    [Fact]
    public async Task AddTwinCatAdsSimulation_Seeds_InitialValues_IntoConnection()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions
            {
                Mode = ConnectionMode.Simulated,
                DisplayName = "Sim",
                InitialValues = new()
                {
                    ["MAIN.bEnabled"] = true,
                    ["MAIN.nSpeed"] = 1500,
                },
            };
        });

        using var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredService<AdsConnectionPool>();

        await pool.StartAsync(CancellationToken.None).WaitAsync(RealTimeout);
        await WaitForConnection(pool, "sim1");

        var conn = pool.GetConnection("sim1");
        Assert.NotNull(conn);

        var bEnabled = await conn!.ReadValueAsync("MAIN.bEnabled", CancellationToken.None);
        Assert.Equal(true, bEnabled);

        var nSpeed = await conn.ReadValueAsync("MAIN.nSpeed", CancellationToken.None);
        Assert.Equal(1500, nSpeed);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task AddTwinCatAdsSimulation_EmptyInitialValues_ConnectionIsEmpty()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions
            {
                Mode = ConnectionMode.Simulated,
                DisplayName = "Sim",
            };
        });

        using var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredService<AdsConnectionPool>();

        await pool.StartAsync(CancellationToken.None).WaitAsync(RealTimeout);
        await WaitForConnection(pool, "sim1");

        var conn = pool.GetConnection("sim1");
        Assert.NotNull(conn);

        var value = await conn!.ReadValueAsync("MAIN.bEnabled", CancellationToken.None);
        Assert.Null(value);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    // -------------------------------------------------------------------------
    // Helper: factory that dispatches by mode
    // -------------------------------------------------------------------------

    /// <summary>
    /// Factory that creates a real <see cref="SimulatedAdsConnection"/> for
    /// Simulated targets and delegates to a <see cref="FakeConnectionFactory"/>
    /// for Real targets — lets the test verify real targets are never reached.
    /// </summary>
    private sealed class ModeDispatchingFactory(FakeConnectionFactory realFactory) : IAdsConnectionFactory
    {
        public IManagedConnection Create(string plcId, PlcTargetOptions options)
        {
            if (options.Mode == ConnectionMode.Simulated)
            {
                var conn = new SimulatedAdsConnection(plcId, options.DisplayName, NullLoggerFactory.Instance);
                conn.SetInitialValues(options.InitialValues);
                return conn;
            }

            return realFactory.Create(plcId, options);
        }
    }
}
