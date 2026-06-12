using Microsoft.Extensions.Logging.Abstractions;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// C21 — structural guard contracts shared by every <see cref="IAdsConnection"/> implementation:
/// empty-input batch shortcuts and the all-null-write guard.
///
/// <see cref="AdsConnection"/> requires hardware, so these contracts are verified against
/// <see cref="SimulatedAdsConnection"/>. The guards (empty input → empty dictionary; a null value →
/// a per-symbol failure recorded WITHOUT attempting the write) are part of the
/// <see cref="IAdsConnection"/> contract and must hold for all implementations.
/// </summary>
public class C21_EmptyBatchShortcutTests
{
    private static SimulatedAdsConnection CreateSim()
        => new("plc1", "PLC 1", new NullLoggerFactory());

    [Fact]
    public async Task ReadValuesAsync_EmptyInput_ReturnsEmptyDictionary()
    {
        using var conn = CreateSim();

        var results = await conn.ReadValuesAsync([], CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task WriteValuesAsync_EmptyInput_ReturnsEmptyDictionary()
    {
        using var conn = CreateSim();

        var results = await conn.WriteValuesAsync(
            new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task WriteValuesAsync_AllNullValues_ReturnsFailures_WithoutAttemptingWrite()
    {
        using var conn = CreateSim();

        var results = await conn.WriteValuesAsync(
            new Dictionary<string, object?> { ["A"] = null, ["B"] = null }, CancellationToken.None);

        // Each null is a per-symbol failure (the null guard fires before any write).
        Assert.Equal(2, results.Count);
        Assert.False(results["A"].Succeeded);
        Assert.IsType<ArgumentNullException>(results["A"].Error);
        Assert.False(results["B"].Succeeded);
        Assert.IsType<ArgumentNullException>(results["B"].Error);

        // No value was ever stored — an untyped read of a never-stored path returns null.
        Assert.Null(await conn.ReadValueAsync("A", CancellationToken.None));
        Assert.Null(await conn.ReadValueAsync("B", CancellationToken.None));
    }
}
