using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TwinCAT.TypeSystem;
using TwinCAT.ValueAccess;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Pins <see cref="NotificationPayload.TryDecodeValue"/>: a notification's value comes from the
/// bytes the notification already carries, decoded by the symbol's OWN Beckhoff value factory, and
/// the decode declines — rather than guessing — whenever those bytes are not the symbol's whole
/// value.
/// </summary>
/// <remarks>
/// The factory is faked here for the same reason <c>AdsConnection</c>'s symbol loader is: a real
/// one only exists on a connected client. What these tests can therefore verify is the CONTRACT —
/// which symbol, which bytes and which timestamp are handed to the factory, that its result is
/// returned unchanged, and precisely when the decode refuses. That the real factory turns those
/// bytes into the same value a read would have is established by Beckhoff's own implementation
/// (<c>ReadValue()</c> is <c>readRaw</c> followed by this very call), documented on
/// <see cref="NotificationPayload"/>, and exercised end to end only by the hardware suite.
/// </remarks>
public class NotificationPayloadTests
{
    private static readonly DateTimeOffset PlcTimestamp =
        new(2026, 7, 28, 9, 30, 15, TimeSpan.FromHours(2));

    private static readonly byte[] Payload = [0xDC, 0x05, 0x00, 0x00];

    [Fact]
    public void Decodes_the_payload_through_the_symbols_own_value_factory()
    {
        var decoded = new object();
        var factory = new FakeValueFactory(decoded);
        var symbol = new StubValueSymbol("MAIN.Speed", DataTypeCategory.Primitive, "DINT", value: null)
        {
            ByteSize = Payload.Length,
            ValueAccessor = new FakeRawValueAccessor(factory),
        };

        Assert.True(NotificationPayload.TryDecodeValue(symbol, Payload, PlcTimestamp, out var value));

        Assert.Same(decoded, value);
        Assert.Equal(1, factory.CreateValueCount);
        Assert.Same(symbol, factory.LastSymbol);
        Assert.Equal(Payload, factory.LastSource);
        Assert.Equal(PlcTimestamp, factory.LastTimeStamp);

        // A notification value has no enclosing value, exactly as for a top-level read.
        Assert.Null(factory.LastParent);

        // A symbol with no external data references has no static data — the same empty argument
        // Beckhoff's own readRaw passes in that case.
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<KeyValuePair<ISymbol, ReadOnlyMemory<byte>>>>(
            factory.LastStaticData));
    }

    [Fact]
    public void Declines_when_the_payload_is_not_the_symbols_whole_storage()
    {
        var factory = new FakeValueFactory(new object());
        var symbol = new StubValueSymbol("MAIN.Speed", DataTypeCategory.Primitive, "DINT", value: null)
        {
            ByteSize = Payload.Length + 2,
            ValueAccessor = new FakeRawValueAccessor(factory),
        };

        Assert.False(NotificationPayload.TryDecodeValue(symbol, Payload, PlcTimestamp, out var value));

        Assert.Null(value);

        // Never handed a partial buffer to decode as if it were complete.
        Assert.Equal(0, factory.CreateValueCount);
    }

    [Fact]
    public void Declines_when_the_symbol_exposes_no_value_accessor()
    {
        // Beckhoff's Symbol.ValueAccessor genuinely returns null when its factory services carry no
        // accessor, and without an accessor there is no factory to decode with.
        var symbol = new StubValueSymbol("MAIN.Speed", DataTypeCategory.Primitive, "DINT", value: null)
        {
            ByteSize = Payload.Length,
        };

        Assert.False(NotificationPayload.TryDecodeValue(symbol, Payload, PlcTimestamp, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Declines_when_part_of_the_symbols_value_lives_outside_its_own_storage()
    {
        // A static member is one of the things Beckhoff's HasExternalDataReferences() reports: for
        // such a symbol a read issues EXTRA requests whose bytes a notification cannot carry, so the
        // payload alone would decode to something a fresh read would not produce.
        var factory = new FakeValueFactory(new object());
        var symbol = new StubValueSymbol("MAIN.Motor", DataTypeCategory.Struct, "ST_Motor", value: null)
        {
            ByteSize = Payload.Length,
            ValueAccessor = new FakeRawValueAccessor(factory),
            IsStatic = true,
        };

        Assert.False(NotificationPayload.TryDecodeValue(symbol, Payload, PlcTimestamp, out var value));

        Assert.Null(value);
        Assert.Equal(0, factory.CreateValueCount);
    }

    [Fact]
    public void Declines_for_a_symbol_that_carries_no_value()
    {
        var symbol = new StubSymbol(DataTypeCategory.Primitive, "DINT") { ByteSize = Payload.Length };

        Assert.False(NotificationPayload.TryDecodeValue(symbol, Payload, PlcTimestamp, out var value));
        Assert.Null(value);
    }
}

/// <summary>
/// Pins <c>AdsConnection.GetNotificationValue</c> — the one value-decode step both
/// <c>SubscribeAsync</c> overloads share: the notification payload is used when it can serve, and a
/// symbol read happens ONLY when it cannot.
/// </summary>
/// <remarks>
/// The registration half of <c>SubscribeAsync</c> needs a connected <c>AdsClient</c> and stays
/// hardware-only, so what is asserted here is the decision this method makes — including, via
/// <see cref="StubValueSymbol.SynchronousReadCount"/>, the POSITIVE fact that the per-notification
/// round-trip Task 10 set out to remove really is gone rather than merely unobserved. That the two
/// handlers call this method is inspection-only.
/// </remarks>
public class AdsConnectionNotificationValueTests
{
    private static readonly DateTimeOffset PlcTimestamp =
        new(2026, 7, 28, 9, 30, 15, TimeSpan.FromHours(2));

    private static readonly byte[] Payload = [0xDC, 0x05, 0x00, 0x00];

    private static AdsConnection CreateConnection() =>
        new("plc1", new PlcTargetOptions { DisplayName = "PLC 1" }, new NullLoggerFactory());

    [Fact]
    public void Takes_the_value_from_the_payload_without_reading_the_symbol()
    {
        var decoded = new object();
        var symbol = StubValueSymbol.WithSynchronousReadValue(
            "MAIN.Speed", DataTypeCategory.Primitive, "DINT", readValue: new object());
        symbol.ByteSize = Payload.Length;
        symbol.ValueAccessor = new FakeRawValueAccessor(new FakeValueFactory(decoded));

        var value = CreateConnection().GetNotificationValue(symbol, Payload, PlcTimestamp);

        Assert.Same(decoded, value);
        Assert.Equal(0, symbol.SynchronousReadCount);
    }

    [Fact]
    public void Falls_back_to_reading_the_symbol_when_the_payload_cannot_serve()
    {
        var fromRead = new object();

        // No value accessor: nothing to decode the payload with.
        var symbol = StubValueSymbol.WithSynchronousReadValue(
            "MAIN.Speed", DataTypeCategory.Primitive, "DINT", fromRead);
        symbol.ByteSize = Payload.Length;

        var value = CreateConnection().GetNotificationValue(symbol, Payload, PlcTimestamp);

        Assert.Same(fromRead, value);
        Assert.Equal(1, symbol.SynchronousReadCount);
    }

    [Fact]
    public void Falls_back_to_reading_the_symbol_when_decoding_the_payload_throws()
    {
        // Losing the optimisation must never mean losing the notification: whatever the payload
        // decode does, the handler still ends up with the value the old re-read produced.
        var fromRead = new object();
        var symbol = StubValueSymbol.WithSynchronousReadValue(
            "MAIN.Speed", DataTypeCategory.Primitive, "DINT", fromRead);
        symbol.ByteSize = Payload.Length;
        symbol.ValueAccessor = new FakeRawValueAccessor(
            FakeValueFactory.ThatThrows(new MarshalException("size mismatch")));

        var value = CreateConnection().GetNotificationValue(symbol, Payload, PlcTimestamp);

        Assert.Same(fromRead, value);
        Assert.Equal(1, symbol.SynchronousReadCount);
    }
}

/// <summary>
/// Minimal <see cref="IAccessorRawValue"/> whose only job is to hand out a
/// <see cref="IAccessorValueFactory"/> — the one member <see cref="NotificationPayload"/> reads off
/// it. Every other member throws: this accessor exists to prove the payload decode needs no wire
/// access, so a decode that started reading raw values here must fail loudly.
/// </summary>
internal sealed class FakeRawValueAccessor(IAccessorValueFactory factory) : IAccessorRawValue
{
    public IAccessorValueFactory ValueFactory => factory;

    // --- Not consumed by NotificationPayload ---------------------------------
    public Encoding DefaultValueEncoding => throw new NotSupportedException();
    public Encoding SymbolEncoding => throw new NotSupportedException();

    public ResultReadRawAccess ReadRaw(ISymbol symbolInstance) => throw new NotSupportedException();
    public Task<ResultReadRawAccess> ReadRawAsync(ISymbol symbolInstance, CancellationToken cancel) =>
        throw new NotSupportedException();
    public ResultReadRawAccess ReadArrayElementRaw(IArrayInstance arrayInstance, int[] indices) =>
        throw new NotSupportedException();
    public Task<ResultReadRawAccess> ReadArrayElementRawAsync(IArrayInstance arrayInstance, int[] indices,
        CancellationToken cancel) => throw new NotSupportedException();

    public int TryWriteRaw(ISymbol symbolInstance, ReadOnlyMemory<byte> writeBuffer,
        out DateTimeOffset? timeStamp) => throw new NotSupportedException();
    public int TryWriteRaw(ISymbol symbolInstance, ReadOnlyMemory<byte> writeBuffer,
        IDictionary<ISymbol, ReadOnlyMemory<byte>>? staticData, out DateTimeOffset? timeStamp) =>
        throw new NotSupportedException();
    public Task<ResultWriteAccess> WriteRawAsync(ISymbol symbolInstance, ReadOnlyMemory<byte> value,
        CancellationToken cancel) => throw new NotSupportedException();
    public Task<ResultWriteAccess> WriteRawAsync(ISymbol symbolInstance, ReadOnlyMemory<byte> value,
        IDictionary<ISymbol, ReadOnlyMemory<byte>>? staticValue, CancellationToken cancel) =>
        throw new NotSupportedException();
    public int TryWriteArrayElementRaw(IArrayInstance arrayInstance, int[] indices,
        ReadOnlyMemory<byte> writeBuffer, out DateTimeOffset? timeStamp) =>
        throw new NotSupportedException();
    public Task<ResultWriteAccess> WriteArrayElementRawAsync(IArrayInstance arrayInstance, int[] indices,
        ReadOnlyMemory<byte> writeBuffer, CancellationToken cancel) => throw new NotSupportedException();
}

/// <summary>
/// Minimal <see cref="IAccessorValueFactory"/> standing in for the real one a connected symbol
/// carries: records what <see cref="NotificationPayload"/> passes to
/// <see cref="CreateValue"/> and returns a canned value (or throws).
/// </summary>
internal sealed class FakeValueFactory : IAccessorValueFactory
{
    private readonly object _value;
    private readonly Exception? _throws;

    public FakeValueFactory(object value)
    {
        _value = value;
    }

    private FakeValueFactory(Exception throws)
    {
        _value = new object();
        _throws = throws;
    }

    /// <summary>
    /// A factory that fails the way the real one fails when the raw data does not fit the symbol.
    /// </summary>
    public static FakeValueFactory ThatThrows(Exception throws) => new(throws);

    public int CreateValueCount { get; private set; }
    public ISymbol? LastSymbol { get; private set; }
    public byte[]? LastSource { get; private set; }
    public IDictionary<ISymbol, ReadOnlyMemory<byte>>? LastStaticData { get; private set; }
    public IValue? LastParent { get; private set; }
    public DateTimeOffset LastTimeStamp { get; private set; }

    public object CreateValue(ISymbol symbol, ReadOnlyMemory<byte> sourceData,
        IDictionary<ISymbol, ReadOnlyMemory<byte>>? sourceStaticData, IValue? parent,
        DateTimeOffset timeStamp)
    {
        CreateValueCount++;
        LastSymbol = symbol;
        LastSource = sourceData.ToArray();
        LastStaticData = sourceStaticData;
        LastParent = parent;
        LastTimeStamp = timeStamp;

        if (_throws is not null)
            throw _throws;

        return _value;
    }

    // --- Not consumed by NotificationPayload ---------------------------------
    public object CreatePrimitiveValue(ISymbol symbol, ReadOnlyMemory<byte> sourceData, IValue? parent,
        DateTimeOffset timeStamp) => throw new NotSupportedException();

    public Encoding DefaultValueEncoding => throw new NotSupportedException();
    public IValueMarshaler ValueMarshaler => throw new NotSupportedException();
    public IConfiguration? Configuration => throw new NotSupportedException();
    public ILoggerFactory? LoggerFactory => throw new NotSupportedException();
}
