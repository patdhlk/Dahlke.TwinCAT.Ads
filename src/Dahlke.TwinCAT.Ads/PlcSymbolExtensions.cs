namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// <see cref="IAdsConnection"/> operations taking a <see cref="PlcSymbol{T}"/> instead of a path
/// string and a type argument.
/// </summary>
/// <remarks>
/// <para>
/// Extension methods rather than interface members, deliberately: the handle is a convenience
/// over the existing contract, and every one of these forwards to the string-based member without
/// adding behaviour. That keeps <see cref="IAdsConnection"/> at one member per operation for
/// implementers and mockers, and means a handle works against ANY implementation — including a
/// consumer's own test double — with nothing to implement.
/// </para>
/// <para>
/// Exception, cancellation and timeout semantics are exactly the string overloads'; see
/// <see cref="IAdsConnection"/>. In particular these compose with
/// <see cref="IAdsConnection.WithTimeout"/>, since they extend the interface the scoped view also
/// implements: <c>conn.WithTimeout(t).ReadValueAsync(Symbols.Big)</c>.
/// </para>
/// </remarks>
public static class PlcSymbolExtensions
{
    /// <summary>
    /// Reads <paramref name="symbol"/> and returns its value as the handle's type.
    /// </summary>
    /// <remarks>
    /// Equivalent to <see cref="IAdsConnection.ReadValueAsync{T}(string, CancellationToken)"/>
    /// with the handle's path and type; see it for the full exception contract.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="symbol"/> is the default value.</exception>
    public static Task<T> ReadValueAsync<T>(
        this IAdsConnection connection, PlcSymbol<T> symbol, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.ReadValueAsync<T>(PathOf(symbol), ct);
    }

    /// <summary>
    /// Writes <paramref name="value"/> to <paramref name="symbol"/>.
    /// </summary>
    /// <remarks>
    /// Equivalent to <see cref="IAdsConnection.WriteValueAsync{T}(string, T, CancellationToken)"/>
    /// with the handle's path and type. The handle's type is what makes the value's type checked
    /// at the call site: writing a <see cref="bool"/> to a <c>PlcSymbol&lt;float&gt;</c> does not
    /// compile.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="symbol"/> is the default value.</exception>
    public static Task WriteValueAsync<T>(
        this IAdsConnection connection, PlcSymbol<T> symbol, T value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.WriteValueAsync(PathOf(symbol), value, ct);
    }

    /// <summary>
    /// Reads <paramref name="symbol"/> together with its PLC type metadata.
    /// </summary>
    /// <remarks>
    /// The result is the same untyped <see cref="AdsValueResult"/> the string overload returns —
    /// the handle names WHICH symbol, but metadata reads report the PLC's own type rather than
    /// the handle's. Use <see cref="ReadValueAsync{T}"/> when the typed value is what is wanted.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="symbol"/> is the default value.</exception>
    public static Task<AdsValueResult> ReadValueWithMetadataAsync<T>(
        this IAdsConnection connection, PlcSymbol<T> symbol, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.ReadValueWithMetadataAsync(PathOf(symbol), ct);
    }

    /// <summary>
    /// Subscribes to value changes for <paramref name="symbol"/>, delivering each value as the
    /// handle's type.
    /// </summary>
    /// <remarks>
    /// Equivalent to
    /// <see cref="IAdsConnection.SubscribeAsync{T}(string, int, Action{string, T}, CancellationToken)"/>
    /// with the handle's path and type — durable across reconnects, and dropping (with a Warning)
    /// any notification whose value will not convert, exactly as that overload documents.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="connection"/> or <paramref name="callback"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="symbol"/> is the default value.</exception>
    public static Task<IDisposable> SubscribeAsync<T>(
        this IAdsConnection connection,
        PlcSymbol<T> symbol,
        int cycleTimeMs,
        Action<string, T?> callback,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(callback);
        return connection.SubscribeAsync(PathOf(symbol), cycleTimeMs, callback, ct);
    }

    /// <summary>
    /// Looks <paramref name="symbol"/> up in a batch result and returns its value as the handle's
    /// type.
    /// </summary>
    /// <remarks>
    /// The batch APIs are keyed by path and carry untyped results, so this is what lets a handle
    /// survive a round trip through one:
    /// <code>
    /// var results = await conn.ReadValuesAsync([Symbols.Setpoint.Path, Symbols.Pump.Path]);
    /// float setpoint = results.GetValue(Symbols.Setpoint);
    /// </code>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="results"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="symbol"/> is the default value.</exception>
    /// <exception cref="KeyNotFoundException">
    /// The batch holds no entry for the symbol's path — it was not among the paths requested.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The entry is a per-symbol failure. The originating exception is the inner exception; this
    /// mirrors <see cref="AdsValueResult.GetValue{T}"/>, which is what this delegates to.
    /// </exception>
    public static T GetValue<T>(
        this IReadOnlyDictionary<string, AdsValueResult> results, PlcSymbol<T> symbol)
    {
        ArgumentNullException.ThrowIfNull(results);
        var path = PathOf(symbol);

        if (!results.TryGetValue(path, out var result))
            throw new KeyNotFoundException(
                $"The batch holds no result for symbol '{path}'. Only the paths passed to the " +
                $"batch call are present; check that this symbol was among them.");

        return result.GetValue<T>();
    }

    /// <summary>
    /// The handle's path, rejecting the <see langword="default"/> value with a message that names
    /// the mistake rather than letting a <see langword="null"/> path reach the ADS layer as a
    /// symbol-not-found.
    /// </summary>
    private static string PathOf<T>(PlcSymbol<T> symbol)
        => symbol.IsEmpty
            ? throw new ArgumentException(
                $"The PlcSymbol<{typeof(T).Name}> is the default value and names no path. " +
                $"Construct it with new PlcSymbol<{typeof(T).Name}>(\"YOUR.SYMBOL.PATH\").",
                nameof(symbol))
            : symbol.Path;
}
