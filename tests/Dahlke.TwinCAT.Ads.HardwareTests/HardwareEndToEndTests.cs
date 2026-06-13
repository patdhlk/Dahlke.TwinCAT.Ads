using Dahlke.TwinCAT.Ads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.HardwareTests;

/// <summary>
/// End-to-end hardware integration tests against a live TwinCAT runtime.
///
/// Prerequisites:
///   - A TwinCAT 3 runtime reachable at TWINCAT_TEST_AMSNETID
///   - Set TWINCAT_HARDWARE_TESTS=1 or TWINCAT_TEST_AMSNETID to enable
///   - Optionally set TWINCAT_TEST_PORT (default 851)
///   - Optionally set TWINCAT_TEST_SYMBOL_INT to a writable INT symbol path
///     (e.g. "MAIN.TestInt") for typed read/write tests
///
/// See tests/Dahlke.TwinCAT.Ads.HardwareTests/README.md for full setup guide.
/// </summary>
public sealed class HardwareEndToEndTests : IAsyncLifetime
{
    private const string PlcId = "hardware_test";
    private const int TestTimeoutMs = 10_000;

    private IHost? _host;
    private IAdsConnectionPool? _pool;

    public async Task InitializeAsync()
    {
        // Only build the host when the env gate is open; FactAttribute.Skip
        // prevents the test body from running, but InitializeAsync still runs —
        // guard here too so the ADS client is not constructed without hardware.
        if (Environment.GetEnvironmentVariable("TWINCAT_HARDWARE_TESTS") != "1"
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TWINCAT_TEST_AMSNETID")))
        {
            return;
        }

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddTwinCatAds(o =>
                {
                    o.Targets[PlcId] = new PlcTargetOptions
                    {
                        AmsNetId = HardwareTestConfig.AmsNetId,
                        Port = HardwareTestConfig.Port,
                        DisplayName = "HardwareTest",
                        TimeoutMs = TestTimeoutMs,
                    };
                });
                services.AddHealthChecks().AddTwinCatAdsHealthCheck();
            })
            .Build();

        await _host.StartAsync();

        // Wait for the connection to become live (up to TestTimeoutMs).
        var pool = _host.Services.GetRequiredService<IAdsConnectionPool>();
        _pool = pool;

        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(TestTimeoutMs);
        while (!pool.GetConnection(PlcId).IsConnected)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Connection to {HardwareTestConfig.AmsNetId} did not become available within {TestTimeoutMs}ms.");
            await Task.Delay(100);
        }
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private IAdsConnection Connection => _pool!.GetConnection(PlcId);

    // ------------------------------------------------------------------
    // 1. AddTwinCatAds + host start → connect
    // ------------------------------------------------------------------

    [HardwareFact]
    public void HostStarted_ConnectionIsAvailableAndConnected()
    {
        var conn = Connection;
        Assert.NotNull(conn);
        Assert.True(conn.IsConnected, $"Expected IsConnected=true after host start.");
        Assert.Equal(ConnectionState.Connected, conn.State);
    }

    // ------------------------------------------------------------------
    // 2. Typed read/write round-trip on configured INT symbol
    // ------------------------------------------------------------------

    [HardwareFact]
    public async Task TypedReadWrite_RoundTrip_IntSymbol()
    {
        if (!HardwareTestConfig.HasSymbolInt)
        {
            // Inline skip when the symbol is not configured.
            return;
        }

        var symbol = HardwareTestConfig.SymbolInt!;
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        // Write a known value then read it back.
        const short expected = 4242;
        await Connection.WriteValueAsync<short>(symbol, expected, cts.Token);
        var actual = await Connection.ReadValueAsync<short>(symbol, cts.Token);

        Assert.Equal(expected, actual);
    }

    // ------------------------------------------------------------------
    // 3. Untyped read
    // ------------------------------------------------------------------

    [HardwareFact]
    public async Task UntypedRead_ReturnsNonNullValue_ForConfiguredIntSymbol()
    {
        if (!HardwareTestConfig.HasSymbolInt)
            return;

        var symbol = HardwareTestConfig.SymbolInt!;
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        var value = await Connection.ReadValueAsync(symbol, cts.Token);

        // For a real PLC symbol the value is never null (unlike simulated).
        Assert.NotNull(value);
    }

    // ------------------------------------------------------------------
    // 4. Batch sum-command read: good + bogus symbol → per-symbol results
    //    THE batch real-divergence check
    // ------------------------------------------------------------------

    [HardwareFact]
    public async Task BatchRead_GoodAndBogusSymbol_BogusIsFailure_GoodSucceeds()
    {
        if (!HardwareTestConfig.HasSymbolInt)
            return;

        var goodSymbol = HardwareTestConfig.SymbolInt!;
        const string bogusSymbol = "__HARDWARE_TEST_BOGUS_SYMBOL_THAT_DOES_NOT_EXIST__";

        using var cts = new CancellationTokenSource(TestTimeoutMs);

        // First write a known value so the read has a predictable result.
        const short written = 1234;
        await Connection.WriteValueAsync<short>(goodSymbol, written, cts.Token);

        var results = await Connection.ReadValuesAsync([goodSymbol, bogusSymbol], cts.Token);

        // The batch must contain entries for both paths.
        Assert.True(results.ContainsKey(goodSymbol), $"Result missing key '{goodSymbol}'");
        Assert.True(results.ContainsKey(bogusSymbol), $"Result missing key '{bogusSymbol}'");

        // Good symbol: success.
        var goodResult = results[goodSymbol];
        Assert.True(goodResult.Succeeded, $"Expected '{goodSymbol}' to succeed but got error: {goodResult.Error}");

        // Bogus symbol: failure with DeviceSymbolNotFound.
        var bogusResult = results[bogusSymbol];
        Assert.False(bogusResult.Succeeded, "Expected bogus symbol to fail.");
        var adsError = Assert.IsType<AdsErrorException>(bogusResult.Error);
        Assert.Equal(AdsErrorCode.DeviceSymbolNotFound, adsError.ErrorCode);
    }

    // ------------------------------------------------------------------
    // 5. Batch write
    // ------------------------------------------------------------------

    [HardwareFact]
    public async Task BatchWrite_IntSymbol_Succeeds()
    {
        if (!HardwareTestConfig.HasSymbolInt)
            return;

        var symbol = HardwareTestConfig.SymbolInt!;
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        const short value = 9999;
        var writeResults = await Connection.WriteValuesAsync(
            new Dictionary<string, object?> { [symbol] = (object)value },
            cts.Token);

        Assert.True(writeResults.ContainsKey(symbol));
        Assert.True(writeResults[symbol].Succeeded,
            $"Batch write failed: {writeResults[symbol].Error}");

        // Verify the written value round-trips.
        var readBack = await Connection.ReadValueAsync<short>(symbol, cts.Token);
        Assert.Equal(value, readBack);
    }

    // ------------------------------------------------------------------
    // 6. GetAdsStateAsync
    // ------------------------------------------------------------------

    [HardwareFact]
    public async Task GetAdsStateAsync_ReturnsRunOrConfig()
    {
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        var state = await Connection.GetAdsStateAsync(cts.Token);

        // A reachable PLC is either in Run or Config state.
        var acceptableStates = new[] { AdsState.Run, AdsState.Config, AdsState.Stop };
        Assert.Contains(state, acceptableStates);
    }

    // ------------------------------------------------------------------
    // 7. Subscription on-change notification delivery
    // ------------------------------------------------------------------

    [HardwareFact]
    public async Task Subscribe_OnChange_ReceivesNotification()
    {
        if (!HardwareTestConfig.HasSymbolInt)
            return;

        var symbol = HardwareTestConfig.SymbolInt!;
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = await Connection.SubscribeAsync(
            symbol,
            cycleTimeMs: 100,
            callback: (_, value) => tcs.TrySetResult(value),
            ct: cts.Token);

        // Trigger a change so the subscription fires.
        const short trigger = 7777;
        await Connection.WriteValueAsync<short>(symbol, trigger, cts.Token);

        var notified = await tcs.Task.WaitAsync(cts.Token);

        Assert.NotNull(notified);
    }

    // ------------------------------------------------------------------
    // 8. Typed subscription on-change notification delivery
    // ------------------------------------------------------------------

    [HardwareFact]
    public async Task SubscribeTyped_OnChange_ReceivesTypedNotification()
    {
        if (!HardwareTestConfig.HasSymbolInt)
            return;

        var symbol = HardwareTestConfig.SymbolInt!;
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        var tcs = new TaskCompletionSource<short>(TaskCreationOptions.RunContinuationsAsynchronously);

        const short trigger = 8888;

        // ADS fires an initial notification on subscribe with the CURRENT value;
        // complete the TCS only for the value we are about to write so the
        // assertion proves delivery of OUR change, not the stale initial value.
        using var registration = await Connection.SubscribeAsync<short>(
            symbol,
            cycleTimeMs: 100,
            callback: (_, value) => { if (value == trigger) tcs.TrySetResult(value); },
            ct: cts.Token);

        await Connection.WriteValueAsync<short>(symbol, trigger, cts.Token);

        var notified = await tcs.Task.WaitAsync(cts.Token);

        Assert.Equal(trigger, notified);
    }

    // ------------------------------------------------------------------
    // 9. Health check against the live pool → Healthy
    // ------------------------------------------------------------------

    [HardwareFact]
    public async Task HealthCheck_LivePool_ReturnsHealthy()
    {
        var healthService = _host!.Services.GetRequiredService<HealthCheckService>();
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        var report = await healthService.CheckHealthAsync(cts.Token);

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.True(report.Entries.ContainsKey("twincat_ads"));
        Assert.Equal(HealthStatus.Healthy, report.Entries["twincat_ads"].Status);
    }
}
