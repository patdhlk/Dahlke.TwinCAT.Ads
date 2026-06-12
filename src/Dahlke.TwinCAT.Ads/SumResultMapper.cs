using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Pure mapping helpers that translate the parallel-array results of a Beckhoff ADS sum
/// command (<c>SumSymbolRead</c> / <c>SumSymbolWrite</c>) into the per-symbol
/// <see cref="AdsValueResult"/> dictionary returned by the batch APIs.
/// </summary>
/// <remarks>
/// <para>
/// A sum command returns results as index-aligned parallel arrays: the value and sub-error at
/// index <c>i</c> belong to the symbol at index <c>i</c>. These helpers re-attach the symbol
/// path to each slot and classify it as a <see cref="AdsValueResult.Success(object?, string?)"/>
/// or <see cref="AdsValueResult.Failure(System.Exception, string?)"/> based on its
/// <see cref="AdsErrorCode"/>.
/// </para>
/// <para>
/// These methods perform no IO and have no dependency on a live connection, so they are fully
/// unit-testable. Exposed as <see langword="internal"/> so tests in
/// <c>Dahlke.TwinCAT.Ads.Tests</c> (via <c>InternalsVisibleTo</c>) can cover the logic directly.
/// </para>
/// <para>
/// The arrays are expected to be index-aligned and of equal length; the implementation reads
/// defensively up to the shortest array so a truncated result never throws an
/// <see cref="IndexOutOfRangeException"/>.
/// </para>
/// </remarks>
internal static class SumResultMapper
{
    /// <summary>
    /// Maps the parallel arrays of a <c>SumSymbolRead</c> result into a per-symbol
    /// <see cref="AdsValueResult"/> dictionary. <paramref name="symbolPaths"/>[i] must correspond
    /// to <paramref name="values"/>[i] and <paramref name="subErrors"/>[i].
    /// </summary>
    /// <param name="symbolPaths">The symbol paths, ordered by symbol index.</param>
    /// <param name="values">The read values, ordered by symbol index. May contain null entries.</param>
    /// <param name="subErrors">The per-symbol ADS error codes, ordered by symbol index.</param>
    /// <returns>
    /// A dictionary keyed by symbol path. A symbol whose sub-error is
    /// <see cref="AdsErrorCode.NoError"/> yields <see cref="AdsValueResult.Success(object?, string?)"/>
    /// carrying its value; any other code yields <see cref="AdsValueResult.Failure(System.Exception, string?)"/>
    /// carrying an <see cref="AdsErrorException"/>.
    /// </returns>
    internal static IReadOnlyDictionary<string, AdsValueResult> MapReadResults(
        string[] symbolPaths,
        object?[] values,
        AdsErrorCode[] subErrors)
    {
        var results = new Dictionary<string, AdsValueResult>(symbolPaths.Length);

        for (var i = 0; i < symbolPaths.Length; i++)
        {
            var path = symbolPaths[i];
            var errorCode = i < subErrors.Length ? subErrors[i] : AdsErrorCode.DeviceError;

            if (errorCode == AdsErrorCode.NoError)
            {
                var value = i < values.Length ? values[i] : null;
                results[path] = AdsValueResult.Success(value, path);
            }
            else
            {
                results[path] = AdsValueResult.Failure(
                    new AdsErrorException(
                        $"Read of symbol '{path}' failed: {errorCode}",
                        errorCode),
                    path);
            }
        }

        return results;
    }

    /// <summary>
    /// Maps the <c>SubErrors</c> array of a <c>SumSymbolWrite</c> result into a per-symbol
    /// <see cref="AdsValueResult"/> dictionary. <paramref name="symbolPaths"/>[i] must correspond
    /// to <paramref name="subErrors"/>[i].
    /// </summary>
    /// <param name="symbolPaths">The symbol paths, ordered by symbol index.</param>
    /// <param name="subErrors">The per-symbol ADS error codes, ordered by symbol index.</param>
    /// <returns>
    /// A dictionary keyed by symbol path. A symbol whose sub-error is
    /// <see cref="AdsErrorCode.NoError"/> yields <see cref="AdsValueResult.Success(object?, string?)"/>
    /// with a <see langword="null"/> value (write success carries no value); any other code yields
    /// <see cref="AdsValueResult.Failure(System.Exception, string?)"/> carrying an
    /// <see cref="AdsErrorException"/>.
    /// </returns>
    internal static IReadOnlyDictionary<string, AdsValueResult> MapWriteResults(
        string[] symbolPaths,
        AdsErrorCode[] subErrors)
    {
        var results = new Dictionary<string, AdsValueResult>(symbolPaths.Length);

        for (var i = 0; i < symbolPaths.Length; i++)
        {
            var path = symbolPaths[i];
            var errorCode = i < subErrors.Length ? subErrors[i] : AdsErrorCode.DeviceError;

            if (errorCode == AdsErrorCode.NoError)
            {
                results[path] = AdsValueResult.Success(null, path);
            }
            else
            {
                results[path] = AdsValueResult.Failure(
                    new AdsErrorException(
                        $"Write of symbol '{path}' failed: {errorCode}",
                        errorCode),
                    path);
            }
        }

        return results;
    }
}
