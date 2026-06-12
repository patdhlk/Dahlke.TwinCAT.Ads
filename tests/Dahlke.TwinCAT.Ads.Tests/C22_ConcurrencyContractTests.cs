using Microsoft.Extensions.Logging.Abstractions;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// C22: Concurrency smoke tests for <see cref="SimulatedAdsConnection"/>.
///
/// Verifies:
/// <list type="number">
///   <item>N parallel writers to different paths + parallel readers: no exception, all values land.</item>
///   <item>Concurrent same-path same-value writers (A→B from N threads) fire callbacks exactly once
///         for the single A→B transition. With the atomic <c>AddOrUpdate</c> pattern the first writer
///         to store B sees previous=A and fires; all subsequent writers see previous=B and do not fire.
///         Assertion: exactly 1 callback for the single transition.</item>
///   <item>Concurrent same-path different-value writers: no exception, final stored value is one of
///         the written values (last writer wins is well-defined).</item>
/// </list>
/// </summary>
public class C22_ConcurrencyContractTests
{
    private static SimulatedAdsConnection CreateConnection()
        => new("test-plc", "Test PLC", NullLoggerFactory.Instance);

    // =========================================================================
    // N parallel writers to DIFFERENT paths + N parallel readers.
    // All writes must land; all reads must succeed and match; no exception thrown.
    // =========================================================================

    [Fact]
    public async Task ParallelWriters_DifferentPaths_AllValuesLand()
    {
        using var conn = CreateConnection();

        const int parallelism = 20;
        var paths = Enumerable.Range(0, parallelism).Select(i => $"MAIN.var{i}").ToArray();

        // Seed all paths to a known value first so reads don't throw.
        conn.SetInitialValues(paths.ToDictionary(p => p, _ => (object?)0));

        // Parallel writers.
        await Task.WhenAll(paths.Select((path, i) =>
            conn.WriteValueAsync(path, i, CancellationToken.None)));

        // Parallel readers — all must return the written value.
        var readTasks = paths.Select(p => conn.ReadValueAsync(p, CancellationToken.None)).ToArray();
        var results = await Task.WhenAll(readTasks);

        for (var i = 0; i < parallelism; i++)
            Assert.Equal(i, results[i]);
    }

    // =========================================================================
    // Concurrent same-path same-value writers (A→B from N threads concurrently).
    // Exactly ONE callback must fire for the single A→B transition.
    // =========================================================================

    [Fact]
    public async Task ConcurrentSamePath_SameNewValue_ExactlyOneCallbackFires()
    {
        using var conn = CreateConnection();

        const string path = "MAIN.valve";
        const int threadCount = 20;

        // Seed initial value "A".
        conn.SetInitialValues(new Dictionary<string, object?> { [path] = "A" });

        var callbackCount = 0;

        using var _ = await conn.SubscribeAsync(path, 0, (_, v) =>
        {
            if (v is "B")
                Interlocked.Increment(ref callbackCount);
        }, CancellationToken.None);

        // All N threads write "B" concurrently.
        await Task.WhenAll(Enumerable.Range(0, threadCount).Select(_ =>
            Task.Run(() => conn.WriteValueAsync(path, "B", CancellationToken.None))));

        // Give any straggler callbacks a moment to complete (callbacks are synchronous
        // on the writer's thread, so by the time all tasks finish all callbacks are done).
        await Task.Delay(10);

        // Exactly one A→B transition should have fired the callback.
        Assert.Equal(1, callbackCount);
    }

    // =========================================================================
    // Concurrent same-path different-value writers: no exception; final value is
    // one of the written values (atomic store, no corruption).
    // =========================================================================

    [Fact]
    public async Task ConcurrentSamePath_DifferentValues_FinalValueIsValid()
    {
        using var conn = CreateConnection();

        const string path = "MAIN.counter";
        const int threadCount = 20;

        var writtenValues = Enumerable.Range(1, threadCount).Select(i => (object)i).ToArray();

        // All threads write different values concurrently — must not throw.
        var tasks = writtenValues.Select(v =>
            Task.Run(() => conn.WriteValueAsync(path, v, CancellationToken.None)));
        var ex = await Record.ExceptionAsync(() => Task.WhenAll(tasks));
        Assert.Null(ex);

        // Final stored value must be one of the values that was written.
        var final = await conn.ReadValueAsync(path, CancellationToken.None);
        Assert.Contains(final, writtenValues);
    }

    // =========================================================================
    // Concurrent batch reads and writes on different paths: no exception, all
    // values visible after the writes complete.
    // =========================================================================

    [Fact]
    public async Task ConcurrentBatchReadWrite_DifferentPaths_NoException()
    {
        using var conn = CreateConnection();

        const int batchSize = 10;
        var writeBatch = Enumerable.Range(0, batchSize)
            .ToDictionary(i => $"MAIN.b{i}", i => (object?)(i * 100));

        // Seed so reads don't see null for missing paths.
        conn.SetInitialValues(writeBatch!);

        var readPaths = Enumerable.Range(0, batchSize).Select(i => $"MAIN.b{i}").ToList();

        // Run a batch write and a batch read concurrently.
        var writeTask = conn.WriteValuesAsync(writeBatch, CancellationToken.None);
        var readTask  = conn.ReadValuesAsync(readPaths, CancellationToken.None);

        // Neither must throw.
        var writeEx = await Record.ExceptionAsync(() => writeTask);
        var readEx  = await Record.ExceptionAsync(() => readTask);

        Assert.Null(writeEx);
        Assert.Null(readEx);

        // After write completes, a fresh read must return the written values.
        var results = await conn.ReadValuesAsync(readPaths, CancellationToken.None);
        for (var i = 0; i < batchSize; i++)
            Assert.Equal(i * 100, results[$"MAIN.b{i}"].Value);
    }
}
