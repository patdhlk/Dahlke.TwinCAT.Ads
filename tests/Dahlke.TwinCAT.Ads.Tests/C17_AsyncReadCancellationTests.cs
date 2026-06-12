using Microsoft.Extensions.Logging.Abstractions;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// C17: Verifies that reads and writes on <see cref="SimulatedAdsConnection"/> genuinely
/// observe the caller's <see cref="CancellationToken"/> and that the cancellation-vs-timeout
/// disambiguation logic in <see cref="CancellationDisambiguator"/> behaves correctly.
///
/// Hardware-only coverage note: <see cref="AdsConnection.ReadValueAsync"/> is not exercised
/// here — it requires a live PLC and is covered in C28 integration tests. What IS verified:
/// - SimulatedAdsConnection.ReadValueAsync observes ct.
/// - SimulatedAdsConnection.WriteValueAsync observes ct.
/// - SimulatedAdsConnection.ReadValuesAsync propagates ct to per-element reads.
/// - SimulatedAdsConnection.WriteValuesAsync observes ct.
/// - CancellationDisambiguator correctly maps (callerCt fired) → OCE and
///   (timeout fired, caller not cancelled) → TimeoutException.
/// </summary>
public class C17_AsyncReadCancellationTests
{
    private static SimulatedAdsConnection CreateConnection()
        => new("test-plc", "Test PLC", NullLoggerFactory.Instance);

    // =========================================================================
    // SimulatedAdsConnection: Read honours ct
    // =========================================================================

    [Fact]
    public async Task ReadValueAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        using var conn = CreateConnection();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => conn.ReadValueAsync("any.symbol", cts.Token));
    }

    [Fact]
    public async Task ReadValueAsync_CancelledToken_ExceptionCarriesCallerToken()
    {
        using var conn = CreateConnection();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(
            () => conn.ReadValueAsync("any.symbol", cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public async Task ReadValueAsync_NotCancelled_ReturnsValue()
    {
        using var conn = CreateConnection();
        conn.SetInitialValues(new Dictionary<string, object?> { ["A.x"] = 99 });

        var result = await conn.ReadValueAsync("A.x", CancellationToken.None);

        Assert.Equal(99, result);
    }

    // =========================================================================
    // SimulatedAdsConnection: Write honours ct
    // =========================================================================

    [Fact]
    public async Task WriteValueAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        using var conn = CreateConnection();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => conn.WriteValueAsync("any.symbol", 42, cts.Token));
    }

    [Fact]
    public async Task WriteValueAsync_CancelledToken_DoesNotStoreValue()
    {
        using var conn = CreateConnection();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await conn.WriteValueAsync("A.x", 42, cts.Token);
        }
        catch (OperationCanceledException) { /* expected */ }

        // Value must NOT have been stored since the token was cancelled first.
        var stored = await conn.ReadValueAsync("A.x", CancellationToken.None);
        Assert.Null(stored);
    }

    // =========================================================================
    // SimulatedAdsConnection: batch operations propagate ct
    // =========================================================================

    [Fact]
    public async Task ReadValuesAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        using var conn = CreateConnection();
        conn.SetInitialValues(new Dictionary<string, object?> { ["A"] = 1, ["B"] = 2 });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => conn.ReadValuesAsync(["A", "B"], cts.Token));
    }

    [Fact]
    public async Task WriteValuesAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        using var conn = CreateConnection();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => conn.WriteValuesAsync(new Dictionary<string, object?> { ["A"] = 1 }, cts.Token));
    }

    // =========================================================================
    // CancellationDisambiguator: unit-tests the timeout-vs-cancellation logic
    // used by AdsConnection.ReadValueAsync (and future hardware code paths).
    // =========================================================================

    [Fact]
    public void Disambiguate_CallerCancelled_ReturnsOperationCanceledExceptionWithCallerToken()
    {
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(callerCts.Token);

        var ex = CancellationDisambiguator.CreateException(
            callerCt: callerCts.Token,
            symbolPath: "MAIN.Valve",
            plcId: "plc1",
            timeoutMs: 500);

        var oce = Assert.IsType<OperationCanceledException>(ex);
        Assert.Equal(callerCts.Token, oce.CancellationToken);
    }

    [Fact]
    public void Disambiguate_TimeoutFired_CallerNotCancelled_ReturnsTimeoutException()
    {
        using var callerCts = new CancellationTokenSource();
        // callerCts NOT cancelled — only the timeout fired

        var ex = CancellationDisambiguator.CreateException(
            callerCt: callerCts.Token,
            symbolPath: "MAIN.Valve",
            plcId: "plc1",
            timeoutMs: 500);

        var te = Assert.IsType<TimeoutException>(ex);
        Assert.Contains("500", te.Message);
        Assert.Contains("MAIN.Valve", te.Message);
        Assert.Contains("plc1", te.Message);
    }

    [Fact]
    public void Disambiguate_TimeoutMessage_ContainsRelevantContext()
    {
        using var callerCts = new CancellationTokenSource();

        var ex = CancellationDisambiguator.CreateException(
            callerCt: callerCts.Token,
            symbolPath: "MAIN.Pressure",
            plcId: "reactor-1",
            timeoutMs: 1234);

        Assert.IsType<TimeoutException>(ex);
        Assert.Contains("MAIN.Pressure", ex.Message);
        Assert.Contains("reactor-1", ex.Message);
        Assert.Contains("1234", ex.Message);
    }
}
