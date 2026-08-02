using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// The standalone entry point is only worth having if it behaves like the hosted one.
/// These are the ways the two could differ: validation that never runs, a configuration
/// ordering that is re-derived rather than delegated, a logging default that swallows,
/// and a failed start that leaks what it managed to start.
/// </summary>
public class AdsConnectionPoolBuilderParityTests
{
    /// <summary>An IHostedService that fails on start, to exercise the unwind path.</summary>
    private sealed class ExplodingHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken ct) =>
            throw new InvalidOperationException("start failed");

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Records whether it was started and stopped, to prove the unwind reaches it.</summary>
    private sealed class ProbeHostedService : IHostedService
    {
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken ct) { Started = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { Stopped = true; return Task.CompletedTask; }
    }

    /// <summary>Disposed by the provider; proves the provider itself was disposed.</summary>
    private sealed class DisposeProbe : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// A hosted service with no job beyond being constructed BY THE CONTAINER, so that
    /// resolving it also resolves — and so makes the container own, and so dispose — the
    /// <see cref="DisposeProbe"/> it depends on. Registering <c>DisposeProbe</c> as a bare
    /// instance (<c>AddSingleton(instance)</c>) would not work even with something like
    /// this: the built-in <see cref="IServiceProvider"/> deliberately never disposes an
    /// instance the caller supplied, only what it built itself. And a factory registration
    /// alone is not enough either, unresolved: nothing else in the graph asks for a
    /// <see cref="DisposeProbe"/>, so without this the factory would simply never run.
    /// </summary>
    private sealed class DisposeProbeOwner : IHostedService
    {
        // Not a primary constructor: the parameter is deliberately unused beyond the
        // resolution it forces, and a primary constructor warns (CS9113) on exactly that.
        public DisposeProbeOwner(DisposeProbe probe) { }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// An options type nothing in the graph ever resolves, so the only way its
    /// validation can run is <see cref="IStartupValidator"/> — see
    /// <see cref="ValidateOnStart_RunsForOptionsNothingResolves"/>.
    /// </summary>
    private sealed class LazyOptions
    {
        public string Value { get; set; } = string.Empty;
    }

    [Fact]
    public async Task InvalidOptions_FailValidationAtStart()
    {
        // A malformed AMS Net ID fails BuildAndStartAsync rather than surfacing later as a
        // connection error blaming the network. This is NOT an isolated regression test for
        // the IStartupValidator call: AdsConnectionPool and AdsRouterService both read
        // IOptions<TwinCatAdsOptions>.Value eagerly in their constructors, so the identical
        // OptionsValidationException is thrown the moment GetServices<IHostedService>()
        // materialises them — with or without the explicit
        // provider.GetService<IStartupValidator>()?.Validate() call. See
        // ValidateOnStart_RunsForOptionsNothingResolves below for the test that actually
        // isolates that call.
        var builder = AdsConnectionPoolBuilder.Create()
            .AddTarget("plc1", o =>
            {
                o.Mode = ConnectionMode.Real;
                o.AmsNetId = "999.1.1.1.1.1";   // an octet out of range
            });

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(
            () => builder.BuildAndStartAsync());

        Assert.Contains("999.1.1.1.1.1", string.Join(" ", ex.Failures));
    }

    [Fact]
    public async Task ValidateOnStart_RunsForOptionsNothingResolves()
    {
        // Isolates the provider.GetService<IStartupValidator>()?.Validate() call. The
        // AMS Net ID test above cannot: the pool and router constructors read
        // options.Value eagerly, so that validation fires with or without the call.
        // Nothing resolves LazyOptions, so ONLY the startup validator can reject it.
        var builder = AdsConnectionPoolBuilder.CreateSimulation()
            .ConfigureServices(s => s.AddOptions<LazyOptions>()
                .Configure(o => o.Value = "bad")
                .Validate(o => o.Value != "bad", "LazyOptions.Value must not be 'bad'.")
                .ValidateOnStart())
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC");

        await Assert.ThrowsAsync<OptionsValidationException>(() => builder.BuildAndStartAsync());
    }

    [Fact]
    public async Task Configuration_BindsBeforeTheLambda()
    {
        // The combo overload's documented ordering, inherited by delegating to it rather
        // than by re-deriving it: binding first, so a lambda sees fully-bound state and a
        // dictionary mutation is not cleared by a later Bind.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlcTargets:plc1:DisplayName"] = "From configuration",
                ["PlcTargets:plc1:Mode"] = "Simulated",
            })
            .Build();

        await using var pool = await AdsConnectionPoolBuilder.Create()
            .UseConfiguration(configuration)
            .AddTarget("plc2", o =>
            {
                o.Mode = ConnectionMode.Simulated;
                o.DisplayName = "From code";
            })
            .BuildAndStartAsync();

        Assert.Equal("From configuration", pool.GetConnection("plc1").DisplayName);
        Assert.Equal("From code", pool.GetConnection("plc2").DisplayName);
    }

    [Fact]
    public async Task Lambda_OverridesAConfiguredValue()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlcTargets:plc1:DisplayName"] = "From configuration",
                ["PlcTargets:plc1:Mode"] = "Simulated",
            })
            .Build();

        await using var pool = await AdsConnectionPoolBuilder.Create()
            .UseConfiguration(configuration)
            .AddTarget("plc1", o => o.DisplayName = "Overridden")
            .BuildAndStartAsync();

        Assert.Equal("Overridden", pool.GetConnection("plc1").DisplayName);
    }

    [Fact]
    public async Task ConfigureDelegates_RunInCallOrder()
    {
        await using var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .Configure(o => o.Targets["plc1"] = new PlcTargetOptions { DisplayName = "first" })
            .Configure(o => o.Targets["plc1"].DisplayName = "second")
            .BuildAndStartAsync();

        Assert.Equal("second", pool.GetConnection("plc1").DisplayName);
    }

    [Fact]
    public async Task UseLoggerFactory_BeatsAConfigureServicesRegistration()
    {
        var mine = new NullLoggerFactory();

        await using var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .ConfigureServices(s => s.AddSingleton<ILoggerFactory>(new NullLoggerFactory()))
            .UseLoggerFactory(mine)
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC")
            .BuildAndStartAsync();

        Assert.Same(mine, pool.Services.GetRequiredService<ILoggerFactory>());
    }

    [Fact]
    public async Task WithNoLoggingConfigured_StartsSilently()
    {
        await using var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC")
            .BuildAndStartAsync();

        Assert.IsType<NullLoggerFactory>(pool.Services.GetRequiredService<ILoggerFactory>());
    }

    [Fact]
    public async Task OpenGenericLogger_Resolves()
    {
        // Logger<T> lives in Logging.Abstractions, so this costs no extra reference — and
        // without it a ConfigureServices registration that takes ILogger<T> (the alarms
        // monitor does) would fail to resolve.
        await using var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC")
            .BuildAndStartAsync();

        Assert.NotNull(pool.Services.GetRequiredService<ILogger<AdsConnectionPoolBuilderParityTests>>());
    }

    [Fact]
    public async Task ConfigureServices_HostedServicesAreStartedAndStopped()
    {
        var probe = new ProbeHostedService();

        var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .ConfigureServices(s => s.AddSingleton<IHostedService>(probe))
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC")
            .BuildAndStartAsync();

        Assert.True(probe.Started);
        Assert.False(probe.Stopped);

        await pool.DisposeAsync();

        Assert.True(probe.Stopped);
    }

    [Fact]
    public async Task FailedStart_StopsWhatStarted_AndDisposesTheProvider()
    {
        var probe = new ProbeHostedService();
        var disposeProbe = new DisposeProbe();

        var builder = AdsConnectionPoolBuilder.CreateSimulation()
            .ConfigureServices(s =>
            {
                // DisposeProbe is registered as a factory, and DisposeProbeOwner exists
                // purely to be constructed by the container so the factory actually runs —
                // see both types' doc comments for why neither alone is enough.
                s.AddSingleton<DisposeProbe>(_ => disposeProbe);
                s.AddSingleton<IHostedService>(probe);
                s.AddSingleton<IHostedService, DisposeProbeOwner>();
                s.AddSingleton<IHostedService>(new ExplodingHostedService());
            })
            .AddTarget("plc1", o => o.DisplayName = "Simulated PLC");

        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAndStartAsync());

        // All three hosted services were registered via ConfigureServices, so
        // BuildAndStartAsync moves them to the end, after the pool — their relative order
        // (probe, then the owner, then the exploder) is preserved, so the probe still
        // starts before the exploder throws. The unwind must stop it, and the provider must
        // be disposed so nothing is left owning resources that the caller never received a
        // handle to.
        Assert.True(probe.Started);
        Assert.True(probe.Stopped);
        Assert.True(disposeProbe.Disposed);
    }

    [Fact]
    public async Task StandaloneAndHosted_ProduceTheSameTargetStates()
    {
        // The strongest parity assertion available without hardware: the same
        // configuration through both entry points yields the same observable pool state.
        await using var standalone = await AdsConnectionPoolBuilder.CreateSimulation()
            .AddTarget("plc1", o => o.DisplayName = "A")
            .AddTarget("plc2", o => o.DisplayName = "B")
            .BuildAndStartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { DisplayName = "A" };
            o.Targets["plc2"] = new PlcTargetOptions { DisplayName = "B" };
        });
        await using var provider = services.BuildServiceProvider();
        var hostedPool = provider.GetRequiredService<AdsConnectionPool>();
        await hostedPool.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(
                hostedPool.GetTargetStates().Select(s => (s.PlcId, s.Mode, s.State)),
                standalone.GetTargetStates().Select(s => (s.PlcId, s.Mode, s.State)));
        }
        finally
        {
            await hostedPool.StopAsync(CancellationToken.None);
        }
    }
}
