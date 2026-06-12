using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// TDD tests for C25: TwinCAT ADS health check with registration extension.
///
/// Covers:
///   - All targets connected → Healthy
///   - Some real/sim targets not connected → Degraded (with ids)
///   - All real targets disconnected → Unhealthy
///   - All-sim config connected → Healthy
///   - Data dictionary contains per-target state entries
///   - Registration test: AddHealthChecks().AddTwinCatAdsHealthCheck() wires up
///     and CheckHealthAsync returns Healthy for a started all-sim pool
/// </summary>
public class C25_HealthCheckTests
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(15);

    // -------------------------------------------------------------------------
    // Pool helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates an <see cref="AdsConnectionPool"/> from the given target descriptors.
    /// Each target is Real by default unless <see cref="ConnectionMode.Simulated"/>
    /// is specified.
    /// </summary>
    private static AdsConnectionPool CreatePool(
        IAdsConnectionFactory factory,
        AdsRouterReadySignal signal,
        params (string Id, ConnectionMode Mode)[] targets)
    {
        var dict = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, mode) in targets)
        {
            dict[id] = new PlcTargetOptions
            {
                Mode = mode,
                AmsNetId = mode == ConnectionMode.Real ? "1.2.3.4.5.6" : string.Empty,
                DisplayName = id,
            };
        }

        return new AdsConnectionPool(
            Options.Create(new TwinCatAdsOptions { Targets = dict }),
            factory,
            signal,
            NullLoggerFactory.Instance,
            TimeProvider.System);
    }

    /// <summary>
    /// Creates a pool with all Real targets (each gets a FakeManagedConnection
    /// that the factory will return on connect).
    /// </summary>
    private static (AdsConnectionPool pool, FakeConnectionFactory factory, AdsRouterReadySignal signal)
        CreateRealPool(params string[] plcIds)
    {
        var factory = new FakeConnectionFactory();
        var signal = new AdsRouterReadySignal();
        var targets = plcIds
            .Select(id => (id, ConnectionMode.Real))
            .ToArray();
        var pool = CreatePool(factory, signal, targets);
        return (pool, factory, signal);
    }

    private static async Task WaitConnected(AdsConnectionPool pool, string plcId)
    {
        var deadline = DateTime.UtcNow + RealTimeout;
        while (pool.GetConnection(plcId) is not { IsConnected: true })
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"'{plcId}' never became connected.");
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    // -------------------------------------------------------------------------
    // Helper: create health check directly from pool
    // -------------------------------------------------------------------------

    private static TwinCatAdsHealthCheck HealthCheck(AdsConnectionPool pool)
        => new(pool);

    private static Task<HealthCheckResult> Check(AdsConnectionPool pool)
        => HealthCheck(pool).CheckHealthAsync(
            new HealthCheckContext
            {
                Registration = new HealthCheckRegistration("test", _ => HealthCheck(pool), null, null)
            });

    // =========================================================================
    // Per-plcId factory: routes connections by target identifier so concurrent
    // connection loops cannot dequeue each other's scripted connections.
    // =========================================================================

    /// <summary>
    /// A factory that dispatches Create calls to a per-plcId inner factory.
    /// This prevents concurrent loops from dequeuing each other's scripted
    /// connections when multiple real targets are configured.
    /// </summary>
    private sealed class PerIdFactory(
        IReadOnlyDictionary<string, FakeConnectionFactory> inner)
        : IAdsConnectionFactory
    {
        public IManagedConnection Create(string plcId, PlcTargetOptions options)
        {
            if (inner.TryGetValue(plcId, out var f))
                return f.Create(plcId, options);

            // Fallback: connected by default (IsAliveDefault = true).
            return new FakeManagedConnection(plcId);
        }
    }

    // =========================================================================
    // Unit tests: health logic against a pool in known states
    // =========================================================================

    [Fact]
    public async Task AllConnected_ReturnsHealthy()
    {
        // Arrange: two real targets, both connected.
        var f1 = new FakeConnectionFactory();
        f1.Enqueue(new FakeManagedConnection("plc1"));
        var f2 = new FakeConnectionFactory();
        f2.Enqueue(new FakeManagedConnection("plc2"));
        var factory = new PerIdFactory(new Dictionary<string, FakeConnectionFactory>
            { ["plc1"] = f1, ["plc2"] = f2 });

        var signal = new AdsRouterReadySignal();
        var pool = CreatePool(factory, signal, ("plc1", ConnectionMode.Real), ("plc2", ConnectionMode.Real));
        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await WaitConnected(pool, "plc1");
        await WaitConnected(pool, "plc2");

        // Act
        var result = await Check(pool);

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("2", result.Description ?? string.Empty);
        Assert.Contains("plc1", result.Data.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("plc2", result.Data.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ConnectionState.Connected.ToString(), result.Data["plc1"]);
        Assert.Equal(ConnectionState.Connected.ToString(), result.Data["plc2"]);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task OneOfTwoDisconnected_ReturnsDegraded_WithDisconnectedIdInDescription()
    {
        // Arrange: plc1 connects successfully; plc2 always fails Connect, stays
        // Disconnected/Connecting. Each target gets its own factory queue so the
        // two concurrent loops cannot dequeue each other's connections.
        var f1 = new FakeConnectionFactory();
        f1.Enqueue(new FakeManagedConnection("plc1"));

        var f2 = new FakeConnectionFactory();
        for (int i = 0; i < 20; i++)
            f2.Enqueue(new FakeManagedConnection("plc2") { ConnectShouldThrow = true });

        var factory = new PerIdFactory(new Dictionary<string, FakeConnectionFactory>
            { ["plc1"] = f1, ["plc2"] = f2 });

        var signal = new AdsRouterReadySignal();
        var pool = CreatePool(factory, signal, ("plc1", ConnectionMode.Real), ("plc2", ConnectionMode.Real));
        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await WaitConnected(pool, "plc1");

        // Wait for plc2 to attempt and fail at least once (Connecting or back to
        // Disconnected — either is "not Connected").
        var deadline = DateTime.UtcNow + RealTimeout;
        while (f2.CreateCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(20));

        // Act: plc1 connected, plc2 not connected
        var result = await Check(pool);

        // Assert
        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.NotNull(result.Description);
        Assert.Contains("plc2", result.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConnectionState.Connected.ToString(), result.Data["plc1"]);
        Assert.NotEqual(ConnectionState.Connected.ToString(), result.Data["plc2"]);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task AllRealDisconnected_ReturnsUnhealthy()
    {
        // Arrange: two real targets, both fail Connect → stay Disconnected.
        // Each target gets its own factory queue so the two concurrent loops
        // cannot dequeue each other's connections (avoids a queue-drain flake).
        var f1 = new FakeConnectionFactory();
        for (int i = 0; i < 20; i++)
            f1.Enqueue(new FakeManagedConnection("plc1") { ConnectShouldThrow = true });

        var f2 = new FakeConnectionFactory();
        for (int i = 0; i < 20; i++)
            f2.Enqueue(new FakeManagedConnection("plc2") { ConnectShouldThrow = true });

        var factory = new PerIdFactory(new Dictionary<string, FakeConnectionFactory>
            { ["plc1"] = f1, ["plc2"] = f2 });

        var signal = new AdsRouterReadySignal();
        var pool = CreatePool(factory, signal, ("plc1", ConnectionMode.Real), ("plc2", ConnectionMode.Real));
        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        // Wait for at least one Connect attempt to have fired (loop started).
        var deadline = DateTime.UtcNow + RealTimeout;
        while (f1.CreateCount == 0 && f2.CreateCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(10));

        // Act
        var result = await Check(pool);

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("plc1", result.Data.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("plc2", result.Data.Keys, StringComparer.OrdinalIgnoreCase);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task FailureStatus_Degraded_AllRealDisconnected_ReturnsDegraded()
    {
        // Arrange: two real targets, both fail Connect → stay Disconnected.
        // Per-target factory queues avoid the concurrent-dequeue flake.
        var f1 = new FakeConnectionFactory();
        for (int i = 0; i < 20; i++)
            f1.Enqueue(new FakeManagedConnection("plc1") { ConnectShouldThrow = true });

        var f2 = new FakeConnectionFactory();
        for (int i = 0; i < 20; i++)
            f2.Enqueue(new FakeManagedConnection("plc2") { ConnectShouldThrow = true });

        var factory = new PerIdFactory(new Dictionary<string, FakeConnectionFactory>
            { ["plc1"] = f1, ["plc2"] = f2 });

        var signal = new AdsRouterReadySignal();
        var pool = CreatePool(factory, signal, ("plc1", ConnectionMode.Real), ("plc2", ConnectionMode.Real));
        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        // Wait for at least one Connect attempt to have fired (loop started).
        var deadline = DateTime.UtcNow + RealTimeout;
        while (f1.CreateCount == 0 && f2.CreateCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(10));

        // Act: drive the check through a registration whose FailureStatus is Degraded.
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "test", _ => HealthCheck(pool), HealthStatus.Degraded, null)
        };
        var result = await HealthCheck(pool).CheckHealthAsync(context);

        // Assert: failure branch honours the configured FailureStatus.
        Assert.Equal(HealthStatus.Degraded, result.Status);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task AllRealDisconnected_RouterLoopsNotReleased_ReturnsUnhealthy_WithRouterNote()
    {
        // Arrange: one real target; signal never set — real loops are NOT released.
        var (pool, factory, signal) = CreateRealPool("plc1");
        // No SetReady → _realLoopsReleased stays false

        await pool.StartAsync(CancellationToken.None);
        // Pool returned promptly; plc1's loop has NOT started yet.

        // Act: health check before any real loop runs
        var result = await Check(pool);

        // Assert: router not ready → real targets disconnected → Unhealthy
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Description);
        Assert.Contains(result.Data.Keys, k => string.Equals(k, "plc1", StringComparison.OrdinalIgnoreCase));

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task AllSimConnected_ReturnsHealthy()
    {
        // Arrange: two simulated targets — they connect instantly.
        var factory = new AdsConnectionFactory(NullLoggerFactory.Instance);
        var signal = new AdsRouterReadySignal();
        var targets = new[]
        {
            ("sim1", ConnectionMode.Simulated),
            ("sim2", ConnectionMode.Simulated),
        };
        var pool = CreatePool(factory, signal, targets);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await WaitConnected(pool, "sim1");
        await WaitConnected(pool, "sim2");

        // Act
        var result = await Check(pool);

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(ConnectionState.Connected.ToString(), result.Data["sim1"]);
        Assert.Equal(ConnectionState.Connected.ToString(), result.Data["sim2"]);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task DataDictionary_ContainsPerTargetStateEntries()
    {
        // Arrange: single real target, connected.
        var (pool, factory, signal) = CreateRealPool("plc1");
        factory.Enqueue(new FakeManagedConnection("plc1"));
        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);
        await WaitConnected(pool, "plc1");

        // Act
        var result = await Check(pool);

        // Assert: data dictionary must have the target id mapped to a state string.
        Assert.Contains("plc1", result.Data.Keys, StringComparer.OrdinalIgnoreCase);
        var stateValue = Assert.IsType<string>(result.Data["plc1"]);
        Assert.Equal(ConnectionState.Connected.ToString(), stateValue);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    // =========================================================================
    // Registration test: end-to-end via DI + HealthCheckService
    // =========================================================================

    [Fact]
    public async Task Registration_AddTwinCatAdsHealthCheck_EndToEnd_AllSimStarted_ReturnsHealthy()
    {
        // Arrange: register TwinCAT ADS (simulation) + health checks.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions { DisplayName = "Sim1" };
        });
        services.AddHealthChecks().AddTwinCatAdsHealthCheck();

        await using var sp = services.BuildServiceProvider();

        // Start the hosted services (connects the sim pool).
        var pool = sp.GetRequiredService<AdsConnectionPool>();
        var signal = sp.GetRequiredService<AdsRouterReadySignal>();

        // Simulation: SetReady is a no-op for the pool (it sets _realLoopsReleased
        // itself for all-sim configs), but the signal is still resolved so the
        // router service doesn't log warnings about a pending wait. In production
        // the AdsRouterService calls SetReady; in tests we drive it directly.
        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        // Wait for the sim target to connect.
        var deadline = DateTime.UtcNow + RealTimeout;
        while (pool.GetConnection("sim1") is not { IsConnected: true })
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("sim1 never became connected.");
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        // Act: run the health check via HealthCheckService.
        var healthService = sp.GetRequiredService<HealthCheckService>();
        var report = await healthService.CheckHealthAsync();

        // Assert
        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.True(report.Entries.ContainsKey("twincat_ads"));
        Assert.Equal(HealthStatus.Healthy, report.Entries["twincat_ads"].Status);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public void Registration_AddTwinCatAdsHealthCheck_CustomName_UsesProvidedName()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions { DisplayName = "Sim1" };
        });
        services.AddHealthChecks().AddTwinCatAdsHealthCheck(name: "my_plc_health");

        using var sp = services.BuildServiceProvider();

        // Act: resolve HealthCheckService options to inspect registrations.
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>();
        var registrations = options.Value.Registrations;

        // Assert: the custom name appears in the registrations.
        Assert.Contains(registrations, r => r.Name == "my_plc_health");
    }

    [Fact]
    public void Registration_AddTwinCatAdsHealthCheck_DefaultName_IsTwinCatAds()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["sim1"] = new PlcTargetOptions { DisplayName = "Sim1" };
        });
        services.AddHealthChecks().AddTwinCatAdsHealthCheck();

        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>();
        var registrations = options.Value.Registrations;

        Assert.Contains(registrations, r => r.Name == "twincat_ads");
    }
}
