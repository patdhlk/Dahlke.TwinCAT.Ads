using System.Collections.Frozen;
using TwinCAT;
using TwinCAT.TypeSystem;
using TwinCAT.ValueAccess;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Turns the raw bytes an ADS notification already carries
/// (<see cref="global::TwinCAT.Ads.AdsNotificationEventArgs.Data"/>) into the value a read of that symbol
/// would have produced — without going back to the wire.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not hand-rolled byte parsing.</b> The decode is done by the symbol's OWN value
/// factory, so PLC struct packing, string encoding, enum backing types and array layout stay
/// Beckhoff's responsibility, exactly as they are for a normal read. On Beckhoff 7.0.292
/// <c>IValueSymbol.ReadValue()</c> is implemented as
/// <c>TwinCAT.ValueAccess.ValueAccessor.readRaw(symbol)</c> followed by
/// <see cref="IAccessorValueFactory.CreateValue"/> — documented as "creates the symbol's value from
/// raw memory data" — over the bytes that read returned. A notification carries those same bytes
/// (the subscription is registered for the symbol's whole storage), so calling
/// <see cref="IAccessorValueFactory.CreateValue"/> with the payload yields the same object the
/// re-read would have yielded, minus the round-trip. The factory is reached from any resolved
/// symbol through the public <see cref="IValueRawSymbol.ValueAccessor"/> →
/// <see cref="IAccessorRawValue.ValueFactory"/> chain.
/// </para>
/// <para>
/// <b>No I/O.</b> The factory installed by <see cref="SymbolsLoadMode.DynamicTree"/> (the mode
/// <c>AdsConnection</c> loads symbols in) unmarshals primitives, strings, sub-ranges and enums in
/// place, builds an array by slicing the same buffer once per element, and wraps a struct /
/// function block / union as a <see cref="DynamicValue"/> holding a copy of the bytes. Nothing on
/// that path touches the connection.
/// </para>
/// <para>
/// <b>When the payload is not enough.</b> <c>readRaw</c> does not always stop at the symbol's own
/// storage: for a symbol with EXTERNAL DATA REFERENCES — a <c>REFERENCE TO</c> member, a static
/// member, a property — it issues additional reads and passes their bytes to
/// <see cref="IAccessorValueFactory.CreateValue"/> as <c>sourceStaticData</c>. A notification
/// payload cannot carry those, so <see cref="TryDecodeValue"/> declines and its caller falls back
/// to reading the symbol. <see cref="DataTypeExtension.HasExternalDataReferences(IInstance)"/> is
/// the same predicate <c>readRaw</c> itself uses, and it also covers
/// <see cref="DataTypeCategory.Reference"/> symbols, whose marshalled size is the RESOLVED size
/// rather than <see cref="IBitSize.ByteSize"/> and so would not match the payload anyway.
/// </para>
/// <para>
/// <b>What this does NOT remove.</b> This yields the value in Beckhoff's own shape — the shape
/// <c>ReadValue()</c> returns. Turning that into this library's neutral tree is still
/// <see cref="PlcValueDecoder"/>'s job, and for a struct or array that walk reads one sub-symbol at
/// a time, which is why container notifications are still decoded off the notification thread.
/// Rebuilding the tree from the <see cref="DynamicValue"/> instead (via
/// <see cref="IStructValue.TryGetMemberValue"/>, which slices the cached bytes with no I/O) was
/// investigated and rejected: for a member whose name collides with one of
/// <see cref="DynamicValue"/>'s own public properties (<c>Symbol</c>, <c>DataType</c>,
/// <c>TimeStamp</c>, <c>Age</c>, <c>CachedRaw</c>, <c>CachedRawStatic</c>, <c>IsPrimitive</c>,
/// <c>UpdateMode</c>, <c>ParentValue</c>, <c>ValueFactory</c>, <c>RootValue</c>) that method does
/// not look the member up at all — it redirects into a rename map that is only ever populated for
/// METHOD collisions, so such a struct throws <see cref="KeyNotFoundException"/> instead of
/// yielding the member. A tree built that way would diverge from a fresh read on exactly the
/// symbols hardest to notice, so the sub-symbol walk stays.
/// </para>
/// </remarks>
internal static class NotificationPayload
{
    /// <summary>
    /// The <c>sourceStaticData</c> argument for a symbol that has no external data references —
    /// what <c>readRaw</c> passes in that case. Immutable and shared: it is only ever enumerated
    /// (and, for a struct, copied) by the factory.
    /// </summary>
    private static readonly FrozenDictionary<ISymbol, ReadOnlyMemory<byte>> NoStaticData =
        FrozenDictionary<ISymbol, ReadOnlyMemory<byte>>.Empty;

    /// <summary>
    /// Decodes <paramref name="payload"/> as <paramref name="symbol"/>'s value, performing no ADS
    /// I/O, and returns <see langword="true"/>. Returns <see langword="false"/> — leaving
    /// <paramref name="value"/> <see langword="null"/> — when the payload cannot serve for this
    /// symbol, in which case the caller must read the symbol instead.
    /// </summary>
    /// <param name="symbol">The resolved symbol the notification is registered for.</param>
    /// <param name="payload">The notification's raw bytes.</param>
    /// <param name="timestamp">
    /// The notification's own timestamp, recorded on the returned value the way a read records the
    /// time the value came back.
    /// </param>
    /// <param name="value">The decoded value, in the same shape <c>ReadValue()</c> returns.</param>
    public static bool TryDecodeValue(ISymbol symbol, ReadOnlyMemory<byte> payload,
        DateTimeOffset timestamp, out object? value)
    {
        value = null;

        // The payload has to be the symbol's whole storage: the factory rejects a length that does
        // not match the symbol, and a partial buffer must never be decoded as if it were complete.
        if (payload.Length != symbol.ByteSize)
            return false;

        // A DynamicSymbol delegates ReadValue() to its inner symbol, and passes THAT symbol — not
        // itself — to the factory. Do the same so the factory sees what it would have seen.
        var target = symbol.Unwrap();

        var factory = (target as IValueRawSymbol)?.ValueAccessor?.ValueFactory;
        if (factory is null)
            return false;

        // Part of this symbol's value lives outside its own storage, so the payload is incomplete.
        if (target.HasExternalDataReferences())
            return false;

        value = factory.CreateValue(target, payload, NoStaticData, parent: null, timestamp);
        return true;
    }
}
