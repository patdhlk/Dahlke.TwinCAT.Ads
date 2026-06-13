using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// The pool no longer blocks startup on the router. SIM loops
/// always start immediately; REAL-target loops are deferred behind a tracked
/// background wait task that releases them once the router signals Ready. While
/// the signal is PENDING, <see cref="AdsConnectionPool.StartAsync"/> returns
/// promptly and real loops are NOT started — they start later, on Ready.
///
/// This is the deliberate behavioural change: a PENDING signal now
/// DEFERS real loops rather than skipping them. (A terminal Failed/Cancelled
/// signal still skips them — see RouterSignalResolutionTests.)
/// </summary>
public class PoolDeferredStartTests
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(10);

    // -------------------------------------------------------------------------
    // Deferred start: real+sim config, signal PENDING. StartAsync returns
    // promptly, sim connects, real loop NOT started. Then SetReady → real loop
    // starts and connects.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PendingSignal_StartReturnsPromptly_SimConnects_RealDeferred_ThenReleasedOnReady()
    {
        var realFactory = new FakeConnectionFactory();
        realFactory.Enqueue(new FakeManagedConnection("real1"));
        var dispatchFactory = new ModeDispatchingFactory(realFactory);

        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal(); // PENDING — never set before StartAsync

        var targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["real1"] = new PlcTargetOptions { Mode = ConnectionMode.Real, AmsNetId = "1.2.3.4.5.6" },
            ["sim1"] = new PlcTargetOptions
            {
                Mode = ConnectionMode.Simulated,
                InitialValues = new() { ["X"] = 11 },
            },
        };
        var adsOptions = new TwinCatAdsOptions { Targets = targets };

        var pool = new AdsConnectionPool(
            Options.Create(adsOptions),
            dispatchFactory,
            signal,
            NullLoggerFactory.Instance,
            time);

        // StartAsync must return promptly even though the signal is PENDING.
        await pool.StartAsync(CancellationToken.None).WaitAsync(RealTimeout);

        // Sim connects immediately.
        await WaitForConnection(pool, "sim1");
        Assert.Equal(11, await pool.GetConnection("sim1").ReadValueAsync("X", CancellationToken.None));

        // Real loop has NOT started: the factory was never called for it.
        await Task.Delay(50);
        Assert.Equal(0, realFactory.CreateCount);
        Assert.False(pool.GetConnection("real1").IsConnected);

        // Router becomes ready → the deferred real loop is released and connects.
        signal.SetReady();
        await WaitForConnection(pool, "real1");
        Assert.True(realFactory.CreateCount >= 1);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    // -------------------------------------------------------------------------
    // Logging: deferred-start path logs an INFO line naming the deferred real
    // ids ("real target loops deferred until router ready").
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PendingSignal_LogsDeferredRealIds()
    {
        var realFactory = new FakeConnectionFactory();
        realFactory.Enqueue(new FakeManagedConnection("real1"));
        var dispatchFactory = new ModeDispatchingFactory(realFactory);

        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal();
        var capturing = new CapturingLoggerFactory();

        var targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["real1"] = new PlcTargetOptions { Mode = ConnectionMode.Real, AmsNetId = "1.2.3.4.5.6" },
        };
        var adsOptions = new TwinCatAdsOptions { Targets = targets };

        var pool = new AdsConnectionPool(
            Options.Create(adsOptions),
            dispatchFactory,
            signal,
            capturing,
            time);

        await pool.StartAsync(CancellationToken.None).WaitAsync(RealTimeout);

        Assert.Contains(
            capturing.Entries,
            e => e.Message.Contains("deferred", StringComparison.OrdinalIgnoreCase)
              && e.Message.Contains("real1", StringComparison.Ordinal));

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    // -------------------------------------------------------------------------
    // Stop mid-wait: real target, signal PENDING, StopAsync before Ready →
    // clean shutdown, no hang, the router wait task is awaited and completes.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StopMidWait_CleanShutdown_NoHang()
    {
        var realFactory = new FakeConnectionFactory();
        realFactory.Enqueue(new FakeManagedConnection("real1"));
        var dispatchFactory = new ModeDispatchingFactory(realFactory);

        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal(); // never set

        var targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["real1"] = new PlcTargetOptions { Mode = ConnectionMode.Real, AmsNetId = "1.2.3.4.5.6" },
            ["sim1"] = new PlcTargetOptions { Mode = ConnectionMode.Simulated },
        };
        var adsOptions = new TwinCatAdsOptions { Targets = targets };

        var pool = new AdsConnectionPool(
            Options.Create(adsOptions),
            dispatchFactory,
            signal,
            NullLoggerFactory.Instance,
            time);

        await pool.StartAsync(CancellationToken.None).WaitAsync(RealTimeout);
        await WaitForConnection(pool, "sim1");

        // Stop while the router wait task is still parked on the pending signal.
        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);

        // Real loop never started.
        Assert.Equal(0, realFactory.CreateCount);
    }

    // -------------------------------------------------------------------------
    // ForceReconnect guard: real target before Ready → warning, no loop; after
    // Ready → normal reconnect.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ForceReconnect_RealTargetBeforeReady_WarnsAndDoesNotStartLoop()
    {
        var realFactory = new FakeConnectionFactory();
        realFactory.Enqueue(new FakeManagedConnection("real1"));
        var dispatchFactory = new ModeDispatchingFactory(realFactory);

        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal(); // PENDING
        var capturing = new CapturingLoggerFactory();

        var targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["real1"] = new PlcTargetOptions { Mode = ConnectionMode.Real, AmsNetId = "1.2.3.4.5.6" },
        };
        var adsOptions = new TwinCatAdsOptions { Targets = targets };

        var pool = new AdsConnectionPool(
            Options.Create(adsOptions),
            dispatchFactory,
            signal,
            capturing,
            time);

        await pool.StartAsync(CancellationToken.None).WaitAsync(RealTimeout);

        // Router not ready yet: ForceReconnect must NOT bypass the gate.
        pool.ForceReconnect("real1");
        await Task.Delay(50);
        Assert.Equal(0, realFactory.CreateCount);
        Assert.Contains(
            capturing.Entries,
            e => e.Message.Contains("router not ready", StringComparison.OrdinalIgnoreCase));

        // After Ready the normal loop starts (released by the wait task).
        signal.SetReady();
        await WaitForConnection(pool, "real1");
        Assert.True(realFactory.CreateCount >= 1);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task ForceReconnect_RealTargetAfterReady_ReconnectsNormally()
    {
        var realFactory = new FakeConnectionFactory();
        realFactory.Enqueue(new FakeManagedConnection("real1"));
        realFactory.Enqueue(new FakeManagedConnection("real1"));
        var dispatchFactory = new ModeDispatchingFactory(realFactory);

        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal();
        signal.SetReady(); // ready before start

        var targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["real1"] = new PlcTargetOptions { Mode = ConnectionMode.Real, AmsNetId = "1.2.3.4.5.6" },
        };
        var adsOptions = new TwinCatAdsOptions { Targets = targets };

        var pool = new AdsConnectionPool(
            Options.Create(adsOptions),
            dispatchFactory,
            signal,
            NullLoggerFactory.Instance,
            time);

        await pool.StartAsync(CancellationToken.None).WaitAsync(RealTimeout);
        await WaitForConnection(pool, "real1");
        var createsAfterStart = realFactory.CreateCount;

        // After Ready, ForceReconnect restarts the loop (normal behaviour).
        pool.ForceReconnect("real1");
        await WaitForRecreate(realFactory, createsAfterStart);

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

    private static async Task WaitForRecreate(FakeConnectionFactory factory, int baseline)
    {
        var deadline = DateTime.UtcNow + RealTimeout;
        while (factory.CreateCount <= baseline)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Factory was not re-invoked after ForceReconnect.");
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
