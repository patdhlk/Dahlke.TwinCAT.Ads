using System.Collections.Concurrent;
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
///   - Optionally set TWINCAT_TEST_SYMBOL_STRUCT to a STRUCT/FUNCTION_BLOCK symbol path
///     and TWINCAT_TEST_SYMBOL_ARRAY to an ARRAY symbol path (both read-only and stable
///     for the run) to close the container notification-decode gate — see
///     <see cref="HardwareTestConfig.SymbolStruct"/> / <see cref="HardwareTestConfig.SymbolArray"/>
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

    /// <summary>
    /// Dequeues notification values until one differs from <paramref name="staleValue"/>, so the
    /// initial notification ADS fires on registration (which carries whatever the symbol held at
    /// that moment) is not mistaken for the one describing the change under test.
    /// </summary>
    private static async Task<object?> NextValueOtherThanAsync(
        ConcurrentQueue<object?> received, SemaphoreSlim arrived, object? staleValue, CancellationToken ct)
    {
        while (true)
        {
            await arrived.WaitAsync(ct);
            if (received.TryDequeue(out var value) && !Equals(value, staleValue))
                return value;
        }
    }

    /// <summary>
    /// Subscribes to <paramref name="symbolPath"/> via the <see cref="AdsNotification"/> overload,
    /// takes the value from the initial notification, then reads the same symbol — and asserts the
    /// two decode to the same tree.
    /// </summary>
    /// <remarks>
    /// Nothing is written: the symbol is expected to be stable for the run (see
    /// <see cref="HardwareTestConfig.SymbolStruct"/>), and ADS delivers one notification on
    /// registration carrying the current value, which is all this comparison needs. That also keeps
    /// the container facts from requiring write access to a struct whose shape the test does not
    /// know.
    /// </remarks>
    private async Task AssertNotificationMatchesReadAsync(string symbolPath, string expectedCategoryHint)
    {
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        var tcs = new TaskCompletionSource<AdsNotification>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = await Connection.SubscribeAsync(
            symbolPath,
            cycleTimeMs: 100,
            callback: n => tcs.TrySetResult(n),
            ct: cts.Token);

        var notification = await tcs.Task.WaitAsync(cts.Token);

        var read = await Connection.ReadValueWithMetadataAsync(symbolPath, cts.Token);
        Assert.True(read.Succeeded, $"Read of '{symbolPath}' failed: {read.Error}");

        Assert.Equal(read.TypeName, notification.TypeName);
        Assert.NotEqual(default, notification.Timestamp);

        AssertTreeEqual(read.Value, notification.Value, symbolPath);

        // Guard the fixture itself: a symbol configured as the struct/array probe that turns out
        // not to be one would make the comparison above pass for the wrong reason.
        Assert.True(
            read.Category?.Contains(expectedCategoryHint, StringComparison.OrdinalIgnoreCase) == true
            || (expectedCategoryHint == "Struct" && read.Category == "FunctionBlock"),
            $"'{symbolPath}' is category '{read.Category}', not the expected {expectedCategoryHint}.");
    }

    /// <summary>
    /// Structural equality over the neutral decoded tree
    /// (<c>Dictionary&lt;string, object?&gt;</c> / <c>object?[]</c> / scalars). The framework's own
    /// equality would compare the nested containers by reference and pass or fail for reasons that
    /// have nothing to do with the values.
    /// </summary>
    private static void AssertTreeEqual(object? expected, object? actual, string path)
    {
        if (expected is IReadOnlyDictionary<string, object?> expectedMembers)
        {
            var actualMembers = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(actual);
            Assert.Equal(
                expectedMembers.Keys.OrderBy(k => k, StringComparer.Ordinal),
                actualMembers.Keys.OrderBy(k => k, StringComparer.Ordinal));

            foreach (var (name, value) in expectedMembers)
                AssertTreeEqual(value, actualMembers[name], $"{path}.{name}");
            return;
        }

        if (expected is object?[] expectedElements)
        {
            var actualElements = Assert.IsType<object?[]>(actual);
            Assert.Equal(expectedElements.Length, actualElements.Length);

            for (var i = 0; i < expectedElements.Length; i++)
                AssertTreeEqual(expectedElements[i], actualElements[i], $"{path}[{i}]");
            return;
        }

        Assert.True(
            Equals(expected, actual),
            $"Notification and read disagree at '{path}': read={expected ?? "<null>"}, notification={actual ?? "<null>"}.");
    }

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

    /// <remarks>
    /// Asserts the notified VALUE, not merely that one arrived. A notification value is produced by
    /// decoding the notification payload with the symbol's own value factory rather than by reading
    /// the symbol back; a decode that returned a wrong-but-non-null value — precisely the
    /// divergence that decode risks — would sail past an <c>Assert.NotNull</c>. The seed write
    /// before the subscription exists so the stale initial notification ADS fires on registration
    /// can be told apart from the one describing our trigger, and so a wrong decode FAILS with the
    /// value it produced rather than hanging until the timeout.
    /// </remarks>
    [HardwareFact]
    public async Task Subscribe_OnChange_DeliversTheValueThatWasWritten()
    {
        if (!HardwareTestConfig.HasSymbolInt)
            return;

        var symbol = HardwareTestConfig.SymbolInt!;
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        const short seed = 1111;
        const short trigger = 7777;

        // Establish a known starting value so the initial notification is identifiable.
        await Connection.WriteValueAsync<short>(symbol, seed, cts.Token);

        var received = new ConcurrentQueue<object?>();
        using var arrived = new SemaphoreSlim(0);

        using var registration = await Connection.SubscribeAsync(
            symbol,
            cycleTimeMs: 100,
            callback: (_, value) => { received.Enqueue(value); arrived.Release(); },
            ct: cts.Token);

        // Trigger a change so the subscription fires.
        await Connection.WriteValueAsync<short>(symbol, trigger, cts.Token);

        var notified = await NextValueOtherThanAsync(received, arrived, seed, cts.Token);

        Assert.Equal(trigger, Assert.IsType<short>(notified));
    }

    // ------------------------------------------------------------------
    // 7b. AdsNotification overload — the typed-notification path the payload
    //     decode was written for. Asserts Value, TypeName and Timestamp.
    // ------------------------------------------------------------------

    [HardwareFact]
    public async Task SubscribeNotification_OnChange_CarriesValueTypeNameAndTimestamp()
    {
        if (!HardwareTestConfig.HasSymbolInt)
            return;

        var symbol = HardwareTestConfig.SymbolInt!;
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        const short seed = 2222;
        const short trigger = 3333;

        await Connection.WriteValueAsync<short>(symbol, seed, cts.Token);

        // Bound the timestamp from below: the change we are about to trigger cannot predate this.
        var beforeWrite = DateTimeOffset.UtcNow;

        var received = new ConcurrentQueue<AdsNotification>();
        using var arrived = new SemaphoreSlim(0);

        using var registration = await Connection.SubscribeAsync(
            symbol,
            cycleTimeMs: 100,
            callback: n => { received.Enqueue(n); arrived.Release(); },
            ct: cts.Token);

        await Connection.WriteValueAsync<short>(symbol, trigger, cts.Token);

        AdsNotification notification;
        while (true)
        {
            await arrived.WaitAsync(cts.Token);
            if (received.TryDequeue(out var candidate) && !Equals(candidate.Value, seed))
            {
                notification = candidate;
                break;
            }
        }

        Assert.Equal(symbol, notification.SymbolPath);
        Assert.Equal(trigger, Assert.IsType<short>(notification.Value));

        // The PLC declares this symbol's type; the notification must report it, not infer it.
        Assert.False(string.IsNullOrWhiteSpace(notification.TypeName));

        // The PLC's own change time. Not measured on arrival, so it must not be default(...)
        // and must sit in a plausible window around the write that caused it. The lower bound is
        // generous by a minute to tolerate PLC/host clock skew, which this test is not policing.
        Assert.NotEqual(default, notification.Timestamp);
        Assert.True(
            notification.Timestamp > beforeWrite.AddMinutes(-1),
            $"Notification timestamp {notification.Timestamp:O} predates the write at {beforeWrite:O} by more than clock skew.");
        Assert.True(
            notification.Timestamp < DateTimeOffset.UtcNow.AddMinutes(1),
            $"Notification timestamp {notification.Timestamp:O} is implausibly far in the future.");
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
    // 8b. Container symbols: the notification decode must agree with a read.
    //
    //     THE decode divergence check. The notification value is built without
    //     going back to the wire for the symbol itself; a read is the ground
    //     truth it claims to reproduce. If IAccessorValueFactory.CreateValue
    //     ever stops matching what readRaw would have produced, these two trees
    //     stop being equal — which is the failure mode no unit test can see,
    //     because the unit tests decode against stubs this library defines.
    // ------------------------------------------------------------------

    [HardwareFact]
    public async Task SubscribeNotification_StructSymbol_DecodesToTheSameTreeAsARead()
    {
        if (!HardwareTestConfig.HasSymbolStruct)
            return;

        await AssertNotificationMatchesReadAsync(HardwareTestConfig.SymbolStruct!, "Struct");
    }

    [HardwareFact]
    public async Task SubscribeNotification_ArraySymbol_DecodesToTheSameTreeAsARead()
    {
        if (!HardwareTestConfig.HasSymbolArray)
            return;

        await AssertNotificationMatchesReadAsync(HardwareTestConfig.SymbolArray!, "Array");
    }

    [HardwareFact]
    public async Task ReadValueWithMetadata_StructSymbol_DecodesToAKeyedTree()
    {
        if (!HardwareTestConfig.HasSymbolStruct)
            return;

        var symbol = HardwareTestConfig.SymbolStruct!;
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        var result = await Connection.ReadValueWithMetadataAsync(symbol, cts.Token);

        Assert.True(result.Succeeded, $"Read of '{symbol}' failed: {result.Error}");
        Assert.False(string.IsNullOrWhiteSpace(result.TypeName));

        // Struct and FunctionBlock both decode to a keyed tree; accept either category.
        Assert.True(
            result.Category is "Struct" or "FunctionBlock",
            $"Expected Struct or FunctionBlock for '{symbol}', got '{result.Category}'.");

        var members = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Value);
        Assert.NotEmpty(members);
    }

    [HardwareFact]
    public async Task ReadValueWithMetadata_ArraySymbol_DecodesToAnObjectArray()
    {
        if (!HardwareTestConfig.HasSymbolArray)
            return;

        var symbol = HardwareTestConfig.SymbolArray!;
        using var cts = new CancellationTokenSource(TestTimeoutMs);

        var result = await Connection.ReadValueWithMetadataAsync(symbol, cts.Token);

        Assert.True(result.Succeeded, $"Read of '{symbol}' failed: {result.Error}");
        Assert.False(string.IsNullOrWhiteSpace(result.TypeName));
        Assert.Equal("Array", result.Category);

        var elements = Assert.IsType<object?[]>(result.Value);
        Assert.NotEmpty(elements);
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
