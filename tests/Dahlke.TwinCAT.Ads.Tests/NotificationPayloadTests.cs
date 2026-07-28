using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TwinCAT.TypeSystem;
using TwinCAT.ValueAccess;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Pins <see cref="NotificationPayload.TryDecodeValue"/>: a notification's value comes from the
/// bytes the notification already carries, decoded by the symbol's OWN Beckhoff value factory, and
/// the decode declines — with a reason, rather than guessing — whenever those bytes are not the
/// symbol's whole value.
/// </summary>
/// <remarks>
/// The factory is faked here for the same reason <c>AdsConnection</c>'s symbol loader is: a real
/// one only exists on a connected client. What these tests can therefore verify is the CONTRACT —
/// which symbol, which bytes and which timestamp are handed to the factory, that its result is
/// returned unchanged, and precisely when and why the decode refuses. That the real factory turns
/// those bytes into the same value a read would have is established by Beckhoff's own
/// implementation (<c>ReadValue()</c> is <c>readRaw</c> followed by this very call), documented on
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

        Assert.True(NotificationPayload.TryDecodeValue(
            symbol, Payload, PlcTimestamp, out var value, out var refusal));

        Assert.Same(decoded, value);
        Assert.Equal(NotificationPayloadRefusal.None, refusal);
        Assert.Equal(1, factory.CreateValueCount);
        Assert.Same(symbol, factory.LastSymbol);
        Assert.Equal(Payload, factory.LastSource);
        Assert.Equal(PlcTimestamp, factory.LastTimeStamp);

        // A notification value has no enclosing value, exactly as for a top-level read.
        Assert.Null(factory.LastParent);

        // A symbol with no external data references has no static data — the same empty (NOT null)
        // argument Beckhoff's own readRaw passes in that case.
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<KeyValuePair<ISymbol, ReadOnlyMemory<byte>>>>(
            factory.LastStaticData));
    }

    [Fact]
    public void Hands_the_factory_the_unwrapped_symbol()
    {
        // Under SymbolsLoadMode.DynamicTree — the mode AdsConnection loads symbols in — the loader
        // yields DynamicSymbols that WRAP the symbol they delegate to, and Beckhoff's own
        // ReadValue() passes the INNER symbol to the value factory, not the wrapper. Unwrapping the
        // same way is what makes this decode argument-for-argument identical to that read, so it
        // needs its own pin: a decode that forgot to unwrap would still decode, still return a
        // value, and still satisfy every other fact here.
        var decoded = new object();
        var innerFactory = new FakeValueFactory(decoded);
        var inner = new StubValueSymbol("MAIN.Speed", DataTypeCategory.Primitive, "DINT", value: null)
        {
            ByteSize = Payload.Length,
            ValueAccessor = new FakeRawValueAccessor(innerFactory),
        };

        // The wrapper is value-capable too, with its OWN factory — as the real DynamicSymbol is. So
        // skipping the unwrap decodes successfully through the WRONG factory rather than failing a
        // type check, which is what makes this a pin on symbol identity.
        var wrapperFactory = new FakeValueFactory(new object());
        var wrapper = new StubDynamicSymbol(inner, DataTypeCategory.Primitive, "DINT")
        {
            InstanceName = "MAIN.Speed",
            ByteSize = Payload.Length,
            ValueAccessor = new FakeRawValueAccessor(wrapperFactory),
        };

        Assert.True(NotificationPayload.TryDecodeValue(
            wrapper, Payload, PlcTimestamp, out var value, out _));

        Assert.Same(decoded, value);
        Assert.Same(inner, innerFactory.LastSymbol);
        Assert.Equal(0, wrapperFactory.CreateValueCount);
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

        Assert.False(NotificationPayload.TryDecodeValue(
            symbol, Payload, PlcTimestamp, out var value, out var refusal));

        Assert.Null(value);
        Assert.Equal(NotificationPayloadRefusal.NotTheWholeValue, refusal);

        // Never handed a partial buffer to decode as if it were complete.
        Assert.Equal(0, factory.CreateValueCount);
    }

    [Fact]
    public void Declines_when_the_symbol_exposes_no_value_accessor()
    {
        // Beckhoff's Symbol.ValueAccessor genuinely returns null unless its factory services are
        // value-capable, and without an accessor there is no factory to decode with.
        var symbol = new StubValueSymbol("MAIN.Speed", DataTypeCategory.Primitive, "DINT", value: null)
        {
            ByteSize = Payload.Length,
        };

        Assert.False(NotificationPayload.TryDecodeValue(
            symbol, Payload, PlcTimestamp, out var value, out var refusal));

        Assert.Null(value);
        Assert.Equal(NotificationPayloadRefusal.NoValueFactory, refusal);
    }

    [Fact]
    public void Declines_when_the_symbol_itself_is_flagged_as_external_data()
    {
        // A static member is the cheap first branch of Beckhoff's HasExternalDataReferences(): for
        // such a symbol a read issues EXTRA requests whose bytes a notification cannot carry, so the
        // payload alone would decode to something a fresh read would not produce.
        var factory = new FakeValueFactory(new object());
        var symbol = new StubValueSymbol("MAIN.Motor", DataTypeCategory.Struct, "ST_Motor", value: null)
        {
            ByteSize = Payload.Length,
            ValueAccessor = new FakeRawValueAccessor(factory),
            IsStatic = true,
        };

        Assert.False(NotificationPayload.TryDecodeValue(
            symbol, Payload, PlcTimestamp, out var value, out var refusal));

        Assert.Null(value);
        Assert.Equal(NotificationPayloadRefusal.ExternalDataReferences, refusal);
        Assert.Equal(0, factory.CreateValueCount);
    }

    [Fact]
    public void Declines_when_the_symbols_data_type_reports_external_data()
    {
        // The OTHER branch of the same predicate, and the one NotificationPayload's remarks single
        // out: a REFERENCE TO symbol, whose value lives at the far end of the reference. It is
        // reached through DataType rather than the IsStatic/IsProperty flags, so without this fact
        // the whole DataType half of the guard is untested and a decode that dropped it would still
        // pass every other fact here.
        var factory = new FakeValueFactory(new object());
        var symbol = new StubValueSymbol(
            "MAIN.MotorRef", DataTypeCategory.Reference, "REFERENCE TO ST_Motor", value: null)
        {
            ByteSize = Payload.Length,
            ValueAccessor = new FakeRawValueAccessor(factory),
            DataType = new StubDataType(DataTypeCategory.Reference),
        };

        Assert.False(NotificationPayload.TryDecodeValue(
            symbol, Payload, PlcTimestamp, out var value, out var refusal));

        Assert.Null(value);
        Assert.Equal(NotificationPayloadRefusal.ExternalDataReferences, refusal);
        Assert.Equal(0, factory.CreateValueCount);
    }

    [Fact]
    public void Declines_for_a_symbol_that_carries_no_value()
    {
        var symbol = new StubSymbol(DataTypeCategory.Primitive, "DINT") { ByteSize = Payload.Length };

        Assert.False(NotificationPayload.TryDecodeValue(
            symbol, Payload, PlcTimestamp, out var value, out var refusal));

        Assert.Null(value);
        Assert.Equal(NotificationPayloadRefusal.NotAValueSymbol, refusal);
    }
}

/// <summary>
/// Pins <c>AdsConnection.GetNotificationValue</c> — the one value-decode step both
/// <c>SubscribeAsync</c> overloads share: the notification payload is used when it can serve, a
/// symbol read happens ONLY when it cannot, and when it cannot that fact is REPORTED — once per
/// subscription, naming the reason.
/// </summary>
/// <remarks>
/// The registration half of <c>SubscribeAsync</c> needs a connected <c>AdsClient</c> and stays
/// hardware-only, so what is asserted here is the decision this method makes — including, via
/// <see cref="StubValueSymbol.SynchronousReadCount"/>, the POSITIVE fact that the per-notification
/// round-trip Task 10 set out to remove really is gone rather than merely unobserved, and the
/// telemetry that makes its ABSENCE observable in the field. That the two handlers call this method,
/// each with a latch owned by its own registration, is inspection-only.
/// </remarks>
public class AdsConnectionNotificationValueTests
{
    private static readonly DateTimeOffset PlcTimestamp =
        new(2026, 7, 28, 9, 30, 15, TimeSpan.FromHours(2));

    private static readonly byte[] Payload = [0xDC, 0x05, 0x00, 0x00];

    private static AdsConnection CreateConnection(ILoggerFactory loggerFactory) =>
        new("plc1", new PlcTargetOptions { DisplayName = "PLC 1" }, loggerFactory);

    [Fact]
    public void Takes_the_value_from_the_payload_without_reading_the_symbol()
    {
        var decoded = new object();
        var symbol = StubValueSymbol.WithSynchronousReadValue(
            "MAIN.Speed", DataTypeCategory.Primitive, "DINT", readValue: new object());
        symbol.ByteSize = Payload.Length;
        symbol.ValueAccessor = new FakeRawValueAccessor(new FakeValueFactory(decoded));

        var logs = new CapturingLoggerFactory();
        var reported = 0;

        var value = CreateConnection(logs).GetNotificationValue(symbol, Payload, PlcTimestamp, ref reported);

        Assert.Same(decoded, value);
        Assert.Equal(0, symbol.SynchronousReadCount);

        // Nothing to report: the fast path must stay silent.
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public void Reports_the_refusal_and_its_reason_once_per_subscription()
    {
        // A refusal is permanent for the symbol, so EVERY notification of this subscription pays a
        // synchronous read. Left silent, that is indistinguishable from the fast path, and this
        // task's whole benefit could be absent in a deployment with nothing to observe. Reported
        // once, an operator learns it without the log filling up at the notification rate.
        var fromRead = new object();
        var symbol = StubValueSymbol.WithSynchronousReadValue(
            "MAIN.Speed", DataTypeCategory.Primitive, "DINT", fromRead);
        symbol.ByteSize = Payload.Length;   // no ValueAccessor: nothing to decode the payload with

        var logs = new CapturingLoggerFactory();
        var connection = CreateConnection(logs);
        var reported = 0;

        for (var i = 0; i < 3; i++)
            Assert.Same(fromRead, connection.GetNotificationValue(symbol, Payload, PlcTimestamp, ref reported));

        Assert.Equal(3, symbol.SynchronousReadCount);

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("MAIN.Speed", entry.Message);

        // Distinguishable: WHICH reason it was, not merely that something happened.
        Assert.Contains(nameof(NotificationPayloadRefusal.NoValueFactory), entry.Message);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public void Reports_again_for_a_different_subscription()
    {
        // The latch belongs to the registration, not to the connection: two subscriptions on one
        // connection must each learn about their own symbol.
        var symbol = StubValueSymbol.WithSynchronousReadValue(
            "MAIN.Speed", DataTypeCategory.Primitive, "DINT", new object());
        symbol.ByteSize = Payload.Length;

        var logs = new CapturingLoggerFactory();
        var connection = CreateConnection(logs);

        var firstSubscription = 0;
        var secondSubscription = 0;
        connection.GetNotificationValue(symbol, Payload, PlcTimestamp, ref firstSubscription);
        connection.GetNotificationValue(symbol, Payload, PlcTimestamp, ref secondSubscription);

        Assert.Equal(2, logs.Entries.Count);
    }

    [Fact]
    public void Falls_back_to_reading_the_symbol_when_decoding_the_payload_throws()
    {
        // Losing the optimisation must never mean losing the notification: whatever the payload
        // decode does, the handler still ends up with the value the old re-read produced. An
        // exception is a shape this code did NOT anticipate, so unlike a refusal it is a Warning.
        var fromRead = new object();
        var symbol = StubValueSymbol.WithSynchronousReadValue(
            "MAIN.Speed", DataTypeCategory.Primitive, "DINT", fromRead);
        symbol.ByteSize = Payload.Length;

        var boom = new MarshalException("size mismatch");
        symbol.ValueAccessor = new FakeRawValueAccessor(FakeValueFactory.ThatThrows(boom));

        var logs = new CapturingLoggerFactory();
        var connection = CreateConnection(logs);
        var reported = 0;

        for (var i = 0; i < 3; i++)
            Assert.Same(fromRead, connection.GetNotificationValue(symbol, Payload, PlcTimestamp, ref reported));

        Assert.Equal(3, symbol.SynchronousReadCount);

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("MAIN.Speed", entry.Message);
        Assert.Same(boom, entry.Exception);
    }

    /// <summary>
    /// Minimal capturing <see cref="ILoggerFactory"/>, local to this test class — the same shape
    /// already used privately in <c>AdsConnectionSymbolBrowsingTests</c> and
    /// <c>PoolDeferredStartTests</c>. Records level, formatted message and exception so a test can
    /// assert on them without a mocking framework.
    /// </summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CapturingLogger(List<(LogLevel, string, Exception?)> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (sink)
                    sink.Add((logLevel, formatter(state, exception), exception));
            }
        }
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
