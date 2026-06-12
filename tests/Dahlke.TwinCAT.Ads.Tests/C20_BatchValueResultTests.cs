using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// C20 — per-symbol batch results (<see cref="AdsValueResult"/>).
///
/// Batch read/write return one <see cref="AdsValueResult"/> per requested symbol so a single
/// bad symbol no longer kills the whole batch. Cancellation, by contrast, aborts the WHOLE
/// batch (it is not a per-symbol failure).
/// </summary>
public class C20_BatchValueResultTests
{
    private static SimulatedAdsConnection CreateSim()
        => new("plc1", "PLC 1", new NullLoggerFactory());

    // ---- AdsValueResult shape -------------------------------------------

    [Fact]
    public void Success_CarriesValue_NoError()
    {
        var r = AdsValueResult.Success(42);
        Assert.True(r.Succeeded);
        Assert.Equal(42, r.Value);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Success_AllowsNullValue()
    {
        var r = AdsValueResult.Success(null);
        Assert.True(r.Succeeded);
        Assert.Null(r.Value);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Failure_CarriesError_NotSucceeded()
    {
        var ex = new InvalidOperationException("boom");
        var r = AdsValueResult.Failure(ex);
        Assert.False(r.Succeeded);
        Assert.Null(r.Value);
        Assert.Same(ex, r.Error);
    }

    [Fact]
    public void GetValue_ConvertsViaConverter()
    {
        var r = AdsValueResult.Success("42");
        Assert.Equal(42, r.GetValue<int>());
    }

    [Fact]
    public void GetValue_OnFailure_ThrowsInvalidOperationWrappingError()
    {
        var ex = new InvalidOperationException("boom");
        var r = AdsValueResult.Failure(ex);
        var thrown = Assert.Throws<InvalidOperationException>(() => r.GetValue<int>());
        Assert.Same(ex, thrown.InnerException);
    }

    // ---- SymbolPath: carried by path-aware factories ---------------------

    [Fact]
    public void AdsValueResult_SymbolPath_SetBySuccessFactory()
    {
        var r = AdsValueResult.Success(42, "A.x");
        Assert.Equal("A.x", r.SymbolPath);
    }

    [Fact]
    public void AdsValueResult_SymbolPath_SetByFailureFactory()
    {
        var r = AdsValueResult.Failure(new InvalidOperationException("boom"), "A.x");
        Assert.Equal("A.x", r.SymbolPath);
    }

    [Fact]
    public void AdsValueResult_SymbolPath_NullWhenOldFactory()
    {
        Assert.Null(AdsValueResult.Success(42).SymbolPath);
    }

    [Fact]
    public void GetValue_UsesSymbolPath_InErrorMessage()
    {
        // A Guid can't convert to int; the conversion error must name the symbol path
        // (not the "<value>" placeholder used when no path is attached).
        var r = AdsValueResult.Success(Guid.NewGuid(), "MAIN.Sensor");
        var ex = Assert.Throws<InvalidCastException>(() => r.GetValue<int>());
        Assert.Contains("MAIN.Sensor", ex.Message);
        Assert.DoesNotContain("<value>", ex.Message);
    }

    // ---- Sim batch read: mixed existing / missing -----------------------

    [Fact]
    public async Task Sim_BatchRead_Mixed_ExistingSucceedWithValue_MissingSucceedWithNull()
    {
        using var conn = CreateSim();
        await conn.WriteValuesAsync(
            new Dictionary<string, object?> { ["X"] = 10, ["Y"] = 20 }, CancellationToken.None);

        var results = await conn.ReadValuesAsync(["X", "Y", "Z"], CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.True(results["X"].Succeeded);
        Assert.Equal(10, results["X"].Value);
        Assert.True(results["Y"].Succeeded);
        Assert.Equal(20, results["Y"].Value);
        // Missing symbol in sim mirrors the untyped single-read: Success(null), not Failure.
        Assert.True(results["Z"].Succeeded);
        Assert.Null(results["Z"].Value);
    }

    [Fact]
    public async Task Sim_BatchRead_DeDuplicatesPaths()
    {
        using var conn = CreateSim();
        await conn.WriteValuesAsync(new Dictionary<string, object?> { ["X"] = 1 }, CancellationToken.None);

        var results = await conn.ReadValuesAsync(["X", "X", "X"], CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(1, results["X"].Value);
    }

    // ---- Sim batch write: all success, keyed per symbol -----------------

    [Fact]
    public async Task Sim_BatchWrite_AllSucceed_KeyedPerSymbol()
    {
        using var conn = CreateSim();

        var results = await conn.WriteValuesAsync(
            new Dictionary<string, object?> { ["A"] = 1, ["B"] = 2 }, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.True(results["A"].Succeeded);
        Assert.Null(results["A"].Value);
        Assert.True(results["B"].Succeeded);
        Assert.Null(results["B"].Value);

        // And the values actually landed.
        var read = await conn.ReadValuesAsync(["A", "B"], CancellationToken.None);
        Assert.Equal(1, read["A"].Value);
        Assert.Equal(2, read["B"].Value);
    }

    // ---- Sim batch write: a null value is a per-symbol failure ----------

    [Fact]
    public async Task Sim_BatchWrite_NullValue_RecordsFailure()
    {
        using var conn = CreateSim();

        var results = await conn.WriteValuesAsync(
            new Dictionary<string, object?> { ["A"] = null }, CancellationToken.None);

        Assert.False(results["A"].Succeeded);
        Assert.IsType<ArgumentNullException>(results["A"].Error);
        Assert.Contains("A", results["A"].Error!.Message);
        // paramName is path-qualified, not the parameter name "values".
        Assert.Equal("values[\"A\"]", ((ArgumentNullException)results["A"].Error!).ParamName);
    }

    [Fact]
    public async Task Sim_BatchWrite_NullValue_DoesNotStoreInSymbolStore()
    {
        using var conn = CreateSim();

        await conn.WriteValuesAsync(
            new Dictionary<string, object?> { ["A"] = null }, CancellationToken.None);

        // The null value was rejected, never stored; the untyped single read of a never-stored
        // path returns null (Success(null) shape, not a stored null).
        var value = await conn.ReadValueAsync("A", CancellationToken.None);
        Assert.Null(value);
    }

    // ---- Cancellation aborts the WHOLE batch (not per-symbol failures) ---

    [Fact]
    public async Task Sim_BatchRead_PreCancelledToken_ThrowsOce_NotPerSymbolFailures()
    {
        using var conn = CreateSim();
        conn.SetInitialValues(new Dictionary<string, object?> { ["A"] = 1, ["B"] = 2 });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => conn.ReadValuesAsync(["A", "B"], cts.Token));
    }

    [Fact]
    public async Task Sim_BatchWrite_PreCancelledToken_ThrowsOce_NotPerSymbolFailures()
    {
        using var conn = CreateSim();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => conn.WriteValuesAsync(new Dictionary<string, object?> { ["A"] = 1 }, cts.Token));
    }

    // ---- Facade: routes through one snapshot; outage waits then throws ---

    [Fact]
    public async Task Facade_BatchRead_RoutesThroughOneSnapshot()
    {
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1", new PlcTargetOptions { DisplayName = "PLC 1", TimeoutMs = 1000 }, time, NullLogger.Instance);

        var sim = CreateSim();
        await sim.WriteValuesAsync(new Dictionary<string, object?> { ["X"] = 7 }, CancellationToken.None);
        facade.SetCurrent(sim);

        var results = await facade.ReadValuesAsync(["X", "Y"], CancellationToken.None);

        Assert.Equal(7, results["X"].Value);
        Assert.True(results["Y"].Succeeded);
        Assert.Null(results["Y"].Value);
    }

    [Fact]
    public async Task Facade_BatchRead_DuringOutage_WaitsThenThrows()
    {
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1", new PlcTargetOptions { DisplayName = "PLC 1", TimeoutMs = 1000 }, time, NullLogger.Instance);

        var task = facade.ReadValuesAsync(["X"], CancellationToken.None);
        Assert.False(task.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1000));
        await Assert.ThrowsAsync<AdsConnectionUnavailableException>(() => task);
    }

    [Fact]
    public async Task Facade_BatchWrite_DuringOutage_WaitsThenThrows()
    {
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1", new PlcTargetOptions { DisplayName = "PLC 1", TimeoutMs = 1000 }, time, NullLogger.Instance);

        var task = facade.WriteValuesAsync(
            new Dictionary<string, object?> { ["X"] = 1 }, CancellationToken.None);
        Assert.False(task.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1000));
        await Assert.ThrowsAsync<AdsConnectionUnavailableException>(() => task);
    }
}
