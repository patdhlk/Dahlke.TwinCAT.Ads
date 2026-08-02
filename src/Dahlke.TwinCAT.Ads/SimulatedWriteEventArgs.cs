namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Describes a single write to a <see cref="SimulatedAdsConnection"/>.
/// </summary>
/// <remarks>
/// <para>
/// Raised for EVERY write, not only for writes that changed the stored value. That is
/// deliberate and it is what distinguishes this event from a subscription: subscriptions
/// deliver on change, so a write of 23.5 over an identical seeded 23.5 is invisible to
/// them. A test asserting "the code under test wrote 23.5" must not depend on what the
/// fixture happened to seed, so this event fires regardless and <see cref="Changed"/>
/// reports which case it was.
/// </para>
/// <para>
/// <b>Threading.</b> Raised synchronously on the writer's thread, immediately after the
/// value is stored and after any subscription callbacks for the same path have run.
/// Handlers must be thread-safe. A handler that throws is caught and logged at Warning;
/// it neither aborts the write nor suppresses other handlers.
/// </para>
/// </remarks>
public sealed class SimulatedWriteEventArgs : EventArgs
{
    /// <summary>
    /// Initialises a new instance of the <see cref="SimulatedWriteEventArgs"/> class.
    /// </summary>
    /// <param name="symbolPath">The symbol path written.</param>
    /// <param name="value">The value written.</param>
    /// <param name="previousValue">The value the symbol held beforehand.</param>
    /// <param name="changed">Whether the write changed the stored value.</param>
    public SimulatedWriteEventArgs(
        string symbolPath,
        object? value,
        object? previousValue,
        bool changed)
    {
        SymbolPath = symbolPath;
        Value = value;
        PreviousValue = previousValue;
        Changed = changed;
    }

    /// <summary>The symbol path that was written.</summary>
    public string SymbolPath { get; }

    /// <summary>The value that was written, boxed as it is stored.</summary>
    public object? Value { get; }

    /// <summary>
    /// The value the symbol held before this write, or <see langword="null"/> when the
    /// symbol had no value at all. Those two cases are not distinguished.
    /// </summary>
    public object? PreviousValue { get; }

    /// <summary>
    /// Whether this write changed the stored value, by the same
    /// <see cref="object.Equals(object, object)"/> rule that decides whether
    /// subscriptions fire. A write of the same value reports <see langword="false"/>
    /// but is still reported.
    /// </summary>
    public bool Changed { get; }
}
