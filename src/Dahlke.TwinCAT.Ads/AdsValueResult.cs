namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// The per-symbol outcome of a batch read or write operation.
/// </summary>
/// <remarks>
/// <para>
/// Batch operations (<see cref="IAdsConnection.ReadValuesAsync"/>,
/// <see cref="IAdsConnection.WriteValuesAsync"/>) report success or failure independently
/// for each requested symbol. One unreadable or unwritable symbol does not abort the whole
/// batch: its slot in the result dictionary carries a <see cref="Failure(Exception, string?)"/> while every other
/// symbol still carries its own <see cref="Success(object?, string?)"/>. This is why a batch returns a
/// dictionary of <see cref="AdsValueResult"/> rather than a flat dictionary of values — the
/// caller can inspect each symbol's outcome without a try/catch around the whole call.
/// </para>
/// <para>
/// A successful read carries the symbol's value in <see cref="Value"/> (which may legitimately
/// be <see langword="null"/>); a successful write carries <see langword="null"/>. A failure
/// carries the originating <see cref="Exception"/> in <see cref="Error"/>.
/// </para>
/// </remarks>
public sealed class AdsValueResult
{
    private AdsValueResult(bool succeeded, object? value, Exception? error, string? symbolPath)
    {
        Succeeded = succeeded;
        Value = value;
        Error = error;
        SymbolPath = symbolPath;
    }

    /// <summary>
    /// <see langword="true"/> when the per-symbol operation completed successfully;
    /// <see langword="false"/> when it failed (in which case <see cref="Error"/> is non-null).
    /// </summary>
    public bool Succeeded { get; }

    /// <summary>
    /// The value read for this symbol on success, or <see langword="null"/>. For a successful
    /// write this is always <see langword="null"/>; for a successful read it may still be
    /// <see langword="null"/> (e.g. a simulated symbol that has never been written). Always
    /// <see langword="null"/> when <see cref="Succeeded"/> is <see langword="false"/>.
    /// </summary>
    public object? Value { get; }

    /// <summary>
    /// The exception that caused this symbol's operation to fail. Non-<see langword="null"/>
    /// if and only if <see cref="Succeeded"/> is <see langword="false"/>.
    /// </summary>
    public Exception? Error { get; }

    /// <summary>
    /// The symbol path this result belongs to, or <see langword="null"/> when the result was
    /// produced by a path-less factory overload (e.g. a free-standing
    /// <c>Success</c> call in a unit test). Batch operations populate this with the
    /// originating path so <see cref="GetValue{T}"/> can surface the symbol in conversion errors.
    /// </summary>
    public string? SymbolPath { get; }

    /// <summary>
    /// Creates a successful result carrying <paramref name="value"/> (which may be
    /// <see langword="null"/>) without an associated symbol path.
    /// </summary>
    public static AdsValueResult Success(object? value) => new(succeeded: true, value, error: null, symbolPath: null);

    /// <summary>
    /// Creates a successful result carrying <paramref name="value"/> (which may be
    /// <see langword="null"/>) for <paramref name="symbolPath"/>.
    /// </summary>
    public static AdsValueResult Success(object? value, string? symbolPath)
        => new(succeeded: true, value, error: null, symbolPath);

    /// <summary>
    /// Creates a failed result carrying <paramref name="error"/> without an associated symbol path.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="error"/> is null.</exception>
    public static AdsValueResult Failure(Exception error)
        => new(succeeded: false, value: null, error ?? throw new ArgumentNullException(nameof(error)), symbolPath: null);

    /// <summary>
    /// Creates a failed result carrying <paramref name="error"/> for <paramref name="symbolPath"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="error"/> is null.</exception>
    public static AdsValueResult Failure(Exception error, string? symbolPath)
        => new(succeeded: false, value: null, error ?? throw new ArgumentNullException(nameof(error)), symbolPath);

    /// <summary>
    /// Converts <see cref="Value"/> to <typeparamref name="T"/> on success using the same
    /// conversion rules as <see cref="IAdsConnection.ReadValueAsync{T}"/> (direct cast,
    /// <see cref="IConvertible"/> widening, invariant-culture string parsing).
    /// </summary>
    /// <typeparam name="T">The .NET type to convert this result's value to.</typeparam>
    /// <returns>The converted value.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when this result is a failure; the originating <see cref="Error"/> is wrapped as
    /// the inner exception.
    /// </exception>
    /// <exception cref="InvalidCastException">
    /// Thrown when this result succeeded but its <see cref="Value"/> cannot be converted to
    /// <typeparamref name="T"/>.
    /// </exception>
    public T GetValue<T>()
    {
        if (!Succeeded)
            throw new InvalidOperationException(
                "Cannot read the value of a failed batch result; inspect Error instead.", Error);

        return AdsValueConverter.ConvertForRead<T>(Value, SymbolPath ?? "<value>");
    }
}
