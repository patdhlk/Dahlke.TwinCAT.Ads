namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// A PLC symbol path together with the .NET type its value is read and written as — the two
/// facts about a symbol that a call site would otherwise repeat as a bare string and a type
/// argument every time it touches it.
/// </summary>
/// <typeparam name="T">
/// The type <see cref="PlcSymbolExtensions.ReadValueAsync{T}"/> converts the value to and
/// <see cref="PlcSymbolExtensions.WriteValueAsync{T}"/> accepts. The same conversion rules apply
/// as for <see cref="IAdsConnection.ReadValueAsync{T}"/> — this binds the type, it does not
/// change how the value is converted.
/// </typeparam>
/// <example>
/// Declare each symbol once, then use it without a path literal or a type argument:
/// <code>
/// static class Symbols
/// {
///     public static readonly PlcSymbol&lt;float&gt; Setpoint = new("GVL.Setpoint");
///     public static readonly PlcSymbol&lt;bool&gt;  Pump     = new("GVL.PumpRunning");
/// }
///
/// float setpoint = await conn.ReadValueAsync(Symbols.Setpoint);
/// await conn.WriteValueAsync(Symbols.Pump, true);
/// </code>
/// </example>
/// <remarks>
/// <para>
/// <b>What this does and does not buy.</b> It removes two classes of mistake that currently
/// surface only at runtime, on hardware: a mistyped path used in one place and spelled correctly
/// in another, and a symbol read as the wrong .NET type. It cannot check the path against the
/// PLC — nothing on this side of the wire can — so an <c>InvalidCastException</c> or a
/// <c>DeviceSymbolNotFound</c> is still possible; what changes is
/// that there is now exactly ONE place per symbol to correct when it happens.
/// </para>
/// <para>
/// <b>Nothing is registered anywhere.</b> A handle is an immutable value, not a subscription,
/// cache entry or resource. Building one is free, it holds no connection, and two handles with
/// the same path and type are equal.
/// </para>
/// <para>
/// <b>The <see langword="default"/> value is not usable.</b> As a struct this type has an
/// unavoidable parameterless default whose <see cref="Path"/> is <see langword="null"/> and which
/// the constructor's validation cannot intercept. Every extension method rejects it with a message
/// naming the problem rather than passing a null path down to the ADS layer. Always construct with
/// <see cref="PlcSymbol{T}(string)"/>.
/// </para>
/// </remarks>
public readonly record struct PlcSymbol<T>
{
    /// <summary>
    /// Creates a handle for <paramref name="path"/>.
    /// </summary>
    /// <param name="path">
    /// The fully-qualified PLC symbol path, e.g. <c>GVL.Setpoint</c> or <c>MAIN.Motor.Speed</c>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty or white space.
    /// </exception>
    public PlcSymbol(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    /// <summary>The fully-qualified PLC symbol path this handle names.</summary>
    public string Path { get; }

    /// <summary>
    /// Whether this is the unusable <see langword="default"/> value rather than a constructed
    /// handle. See the type's remarks.
    /// </summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Path);

    /// <summary>The symbol path, so a handle interpolates into a log message as its path.</summary>
    public override string ToString() => Path ?? string.Empty;
}
