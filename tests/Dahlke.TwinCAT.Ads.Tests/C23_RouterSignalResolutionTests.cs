using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// TDD tests for C23: deterministic signal resolution across the
/// <see cref="AdsRouterService"/> exit paths, and the pool's tri-state
/// reaction (Failed logs the reason; Failed and Cancelled both route into the
/// "router not available" path).
/// </summary>
public class C23_RouterSignalResolutionTests
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(10);

    // -------------------------------------------------------------------------
    // Service: a pre-cancelled stopping token must still RESOLVE the signal
    // (Cancelled), never leave it pending.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RouterService_PreCancelledStoppingToken_ResolvesSignal_NotPending()
    {
        var signal = new AdsRouterReadySignal();

        // Real target with a configured NetId so the service takes the router
        // branch (not the "no real targets" early SetReady).
        var options = new TwinCatAdsOptions
        {
            Targets = new(StringComparer.OrdinalIgnoreCase)
            {
                ["real1"] = new PlcTargetOptions { Mode = ConnectionMode.Real, AmsNetId = "1.2.3.4.5.6" },
            },
            Router = new AmsRouterOptions { NetId = "127.0.0.1.1.1" },
        };

        var svc = new AdsRouterService(
            Options.Create(options),
            configuration: null,
            NullLoggerFactory.Instance,
            signal,
            new FakeTimeProvider());

        // Pre-cancelled token: the OLD code's `await Task.Delay(1, token)` outside
        // the try would throw before any SetReady/SetFailed, leaving the signal
        // forever pending.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await svc.StartAsync(cts.Token);

        // The signal MUST be resolved. WaitAsync(None) must not hang. Cancelled
        // surfaces as TaskCanceledException; that is a resolution, not a hang.
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => signal.WaitAsync(CancellationToken.None).WaitAsync(RealTimeout));

        await svc.StopAsync(CancellationToken.None);
    }

    // -------------------------------------------------------------------------
    // Pool: router-Failed path logs the actual reason AND starts sim targets.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Pool_RouterFailed_LogsReason_AndStartsSimTargets()
    {
        var realFactory = new FakeConnectionFactory();
        realFactory.Enqueue(new FakeManagedConnection("real1"));
        var dispatchFactory = new ModeDispatchingFactory(realFactory);

        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal();
        var capturingFactory = new CapturingLoggerFactory();

        var targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["real1"] = new PlcTargetOptions { Mode = ConnectionMode.Real, AmsNetId = "1.2.3.4.5.6" },
            ["sim1"] = new PlcTargetOptions
            {
                Mode = ConnectionMode.Simulated,
                InitialValues = new() { ["X"] = 7 },
            },
        };
        var adsOptions = new TwinCatAdsOptions { Targets = targets };

        var pool = new AdsConnectionPool(
            Options.Create(adsOptions),
            dispatchFactory,
            signal,
            capturingFactory,
            time);

        // Router failed with a concrete, identifiable reason.
        const string reasonText = "AmsTcpIpRouter port 48898 already in use";
        signal.SetFailed(new InvalidOperationException(reasonText));

        await pool.StartAsync(CancellationToken.None).WaitAsync(RealTimeout);

        // Sim target still connects.
        await WaitForConnection(pool, "sim1");
        var simConn = pool.GetConnection("sim1");
        Assert.True(simConn.IsConnected);
        Assert.Equal(7, await simConn.ReadValueAsync("X", CancellationToken.None));

        // The reason was logged somewhere by the pool.
        Assert.Contains(
            capturingFactory.Entries,
            e => e.Message.Contains(reasonText, StringComparison.Ordinal));

        // Real target was skipped.
        Assert.Equal(0, realFactory.CreateCount);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    // -------------------------------------------------------------------------
    // Pool: signal-level Cancelled routes into the same "not available" path,
    // sim targets still start (no reason to log).
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Pool_RouterCancelled_StartsSimTargets_SkipsReal()
    {
        var realFactory = new FakeConnectionFactory();
        realFactory.Enqueue(new FakeManagedConnection("real1"));
        var dispatchFactory = new ModeDispatchingFactory(realFactory);

        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal();

        var targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["real1"] = new PlcTargetOptions { Mode = ConnectionMode.Real, AmsNetId = "1.2.3.4.5.6" },
            ["sim1"] = new PlcTargetOptions
            {
                Mode = ConnectionMode.Simulated,
                InitialValues = new() { ["X"] = 99 },
            },
        };
        var adsOptions = new TwinCatAdsOptions { Targets = targets };

        var pool = new AdsConnectionPool(
            Options.Create(adsOptions),
            dispatchFactory,
            signal,
            NullLoggerFactory.Instance,
            time);

        signal.SetCancelled();

        await pool.StartAsync(CancellationToken.None).WaitAsync(RealTimeout);

        await WaitForConnection(pool, "sim1");
        var simConn = pool.GetConnection("sim1");
        Assert.True(simConn.IsConnected);
        Assert.Equal(99, await simConn.ReadValueAsync("X", CancellationToken.None));

        Assert.Equal(0, realFactory.CreateCount);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

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

    /// <summary>
    /// Logger factory that captures every formatted log message so a test can
    /// assert the pool logged the router failure reason.
    /// </summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<(string Category, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CapturingLogger(string category, List<(string, string)> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (sink)
                    sink.Add((category, formatter(state, exception)));
            }
        }
    }
}
