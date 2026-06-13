using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Pure mapping of ADS sum-command parallel arrays into per-symbol
/// <see cref="AdsValueResult"/> dictionaries via <see cref="SumResultMapper"/>.
///
/// These tests need no hardware: they exercise the index-aligned array → dictionary logic
/// directly, covering all-success, mixed, all-failure, and empty inputs for both read and write.
/// </summary>
public class SumResultMapperTests
{
    // ---- MapReadResults ---------------------------------------------------

    [Fact]
    public void MapReadResults_AllSuccess_ReturnsSuccessResults()
    {
        string[] paths = ["A.x", "A.y", "A.z"];
        object?[] values = [10, "hello", 3.14];
        AdsErrorCode[] subErrors = [AdsErrorCode.NoError, AdsErrorCode.NoError, AdsErrorCode.NoError];

        var results = SumResultMapper.MapReadResults(paths, values, subErrors);

        Assert.Equal(3, results.Count);
        Assert.True(results["A.x"].Succeeded);
        Assert.Equal(10, results["A.x"].Value);
        Assert.Equal("A.x", results["A.x"].SymbolPath);
        Assert.True(results["A.y"].Succeeded);
        Assert.Equal("hello", results["A.y"].Value);
        Assert.True(results["A.z"].Succeeded);
        Assert.Equal(3.14, results["A.z"].Value);
    }

    [Fact]
    public void MapReadResults_SomeFailures_MixedResults()
    {
        string[] paths = ["A.x", "A.y", "A.z"];
        object?[] values = [10, null, 30];
        AdsErrorCode[] subErrors =
        [
            AdsErrorCode.NoError,
            AdsErrorCode.DeviceSymbolNotFound,
            AdsErrorCode.NoError,
        ];

        var results = SumResultMapper.MapReadResults(paths, values, subErrors);

        Assert.True(results["A.x"].Succeeded);
        Assert.Equal(10, results["A.x"].Value);

        Assert.False(results["A.y"].Succeeded);
        Assert.Null(results["A.y"].Value);
        Assert.IsType<AdsErrorException>(results["A.y"].Error);

        Assert.True(results["A.z"].Succeeded);
        Assert.Equal(30, results["A.z"].Value);
    }

    [Fact]
    public void MapReadResults_AllFailures_AllFailureResults()
    {
        string[] paths = ["A.x", "A.y"];
        object?[] values = [null, null];
        AdsErrorCode[] subErrors = [AdsErrorCode.DeviceInvalidParam, AdsErrorCode.DeviceSymbolNotFound];

        var results = SumResultMapper.MapReadResults(paths, values, subErrors);

        Assert.Equal(2, results.Count);
        Assert.All(results.Values, r => Assert.False(r.Succeeded));
        Assert.All(results.Values, r => Assert.IsType<AdsErrorException>(r.Error));
    }

    [Fact]
    public void MapReadResults_EmptyInputs_ReturnsEmptyDictionary()
    {
        var results = SumResultMapper.MapReadResults([], [], []);
        Assert.Empty(results);
    }

    [Fact]
    public void MapReadResults_FailureResult_CarriesAdsErrorException_WithCorrectErrorCode()
    {
        string[] paths = ["MAIN.Sensor"];
        object?[] values = [null];
        AdsErrorCode[] subErrors = [AdsErrorCode.DeviceSymbolNotFound];

        var results = SumResultMapper.MapReadResults(paths, values, subErrors);

        var error = Assert.IsType<AdsErrorException>(results["MAIN.Sensor"].Error);
        Assert.Equal(AdsErrorCode.DeviceSymbolNotFound, error.ErrorCode);
        Assert.Contains("MAIN.Sensor", error.Message);
        Assert.Equal("MAIN.Sensor", results["MAIN.Sensor"].SymbolPath);
    }

    // ---- MapWriteResults --------------------------------------------------

    [Fact]
    public void MapWriteResults_AllSuccess_ReturnsSuccessResults()
    {
        string[] paths = ["A.x", "A.y"];
        AdsErrorCode[] subErrors = [AdsErrorCode.NoError, AdsErrorCode.NoError];

        var results = SumResultMapper.MapWriteResults(paths, subErrors);

        Assert.Equal(2, results.Count);
        Assert.True(results["A.x"].Succeeded);
        Assert.True(results["A.y"].Succeeded);
        Assert.Equal("A.x", results["A.x"].SymbolPath);
    }

    [Fact]
    public void MapWriteResults_SomeFailures_MixedResults()
    {
        string[] paths = ["A.x", "A.y", "A.z"];
        AdsErrorCode[] subErrors =
        [
            AdsErrorCode.NoError,
            AdsErrorCode.DeviceInvalidParam,
            AdsErrorCode.NoError,
        ];

        var results = SumResultMapper.MapWriteResults(paths, subErrors);

        Assert.True(results["A.x"].Succeeded);
        Assert.False(results["A.y"].Succeeded);
        var error = Assert.IsType<AdsErrorException>(results["A.y"].Error);
        Assert.Equal(AdsErrorCode.DeviceInvalidParam, error.ErrorCode);
        Assert.True(results["A.z"].Succeeded);
    }

    [Fact]
    public void MapWriteResults_EmptyInputs_ReturnsEmptyDictionary()
    {
        var results = SumResultMapper.MapWriteResults([], []);
        Assert.Empty(results);
    }

    [Fact]
    public void MapWriteResults_SuccessResult_HasNullValue()
    {
        string[] paths = ["A.x"];
        AdsErrorCode[] subErrors = [AdsErrorCode.NoError];

        var results = SumResultMapper.MapWriteResults(paths, subErrors);

        Assert.True(results["A.x"].Succeeded);
        // A successful write carries no value.
        Assert.Null(results["A.x"].Value);
    }
}
