using System.Collections;
using System.Text;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.TypeSystem;
using TwinCAT.ValueAccess;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Minimal <see cref="ISymbol"/> stub exposing what <see cref="PlcValueDecoder"/>, the
/// batch-read partition in <c>AdsConnection.ReadValuesAsync</c>, and (since Task 6) the symbol
/// browsing in <c>AdsConnection.GetSymbolsAsync</c>/<c>SearchSymbolsAsync</c> genuinely read:
/// <see cref="Category"/>, <see cref="TypeName"/>, <see cref="InstanceName"/>,
/// <see cref="InstancePath"/>, <see cref="ByteSize"/>, <see cref="Comment"/>, and
/// <see cref="SubSymbols"/> (its <c>Count</c>, indexer, and enumeration — see
/// <see cref="StubSymbolCollection"/>), plus (on
/// <see cref="StubValueSymbol"/>) the async <c>ReadValueAsync(CancellationToken)</c>
/// <see cref="PlcValueDecoder"/> now uses to read struct members / array elements. Everything
/// else throws so that any new dependency either consumer grows fails loudly rather than
/// silently passing on a default.
/// </summary>
/// <remarks>
/// Since Task 10, <see cref="NotificationPayload"/> is a third consumer, and it reads three more
/// members: <see cref="IsStatic"/>, <see cref="IsProperty"/> and <see cref="DataType"/> — the
/// inputs to Beckhoff's <c>HasExternalDataReferences()</c>, which decides whether a notification
/// payload covers the symbol's whole value. All three are now plain settable properties defaulting
/// to "no external data references" (<see langword="false"/>, <see langword="false"/>,
/// <see langword="null"/> — a null <c>DataType</c> makes that predicate return
/// <see langword="false"/> without needing an <see cref="IDataType"/> fake).
/// </remarks>
internal class StubSymbol : ISymbol
{
    public StubSymbol(DataTypeCategory category, string typeName, params ISymbol[] subSymbols)
    {
        Category = category;
        TypeName = typeName;
        SubSymbols = new StubSymbolCollection(subSymbols);
    }

    public DataTypeCategory Category { get; }
    public string TypeName { get; }
    public string InstanceName { get; set; } = "Stub";
    public ISymbolCollection<ISymbol> SubSymbols { get; }

    // Genuinely read by AdsSymbolInfo mapping (Task 6): AdsConnection.MapSymbol reads
    // InstancePath, ByteSize and Comment for every browsed/searched symbol. InstancePath mirrors
    // InstanceName — every test that browses a symbol already sets InstanceName to the full
    // dotted path (see e.g. ReadValueWithMetadataAsyncTests), so no separate field is needed.
    // ByteSize/Comment are plain settable properties (defaulting to 0/"") so tests that don't
    // care about them need not set anything.
    public string InstancePath => InstanceName;
    public int ByteSize { get; set; }
    public string Comment { get; set; } = "";

    // Genuinely read since Task 10: NotificationPayload.TryDecodeValue asks Beckhoff's
    // HasExternalDataReferences() whether the notification payload covers the symbol's whole value,
    // and that predicate reads exactly these three. The defaults describe an ordinary symbol whose
    // value lives entirely in its own storage.
    public bool IsStatic { get; set; }
    public bool IsProperty { get; set; }
    public IDataType? DataType { get; set; }

    // --- Not consumed by PlcValueDecoder -------------------------------------
    public ISymbol? Parent => throw new NotSupportedException();
    public bool IsContainerType => throw new NotSupportedException();
    public bool IsPrimitiveType => throw new NotSupportedException();
    public bool IsPersistent => throw new NotSupportedException();
    public bool IsReadOnly => throw new NotSupportedException();
    public bool IsRecursive => throw new NotSupportedException();
    public bool IsReference => throw new NotSupportedException();
    public bool IsPointer => throw new NotSupportedException();
    public bool IsBitType => throw new NotSupportedException();
    public bool IsByteAligned => throw new NotSupportedException();
    public int Size => throw new NotSupportedException();
    public int BitSize => throw new NotSupportedException();
    public ITypeAttributeCollection Attributes => throw new NotSupportedException();
    public Encoding ValueEncoding => throw new NotSupportedException();
}

internal sealed class StubValueSymbol : StubSymbol, IValueSymbol
{
    private readonly object? _value;
    private readonly bool _failRead;
    private readonly bool _neverCompletesRead;

    public StubValueSymbol(string instanceName, DataTypeCategory category, string typeName,
        object? value, params ISymbol[] subSymbols)
        : base(category, typeName, subSymbols)
    {
        InstanceName = instanceName;
        _value = value;
    }

    private StubValueSymbol(string instanceName, DataTypeCategory category, string typeName,
        bool failRead, bool neverCompletesRead)
        : base(category, typeName)
    {
        InstanceName = instanceName;
        _failRead = failRead;
        _neverCompletesRead = neverCompletesRead;
    }

    /// <summary>
    /// A member whose read fails with <see cref="AdsErrorCode.DeviceError"/> — used to pin that
    /// <see cref="PlcValueDecoder"/> surfaces a failed member read as an
    /// <see cref="AdsErrorException"/>, the same way the old synchronous, throwing
    /// <c>ReadValue()</c> used to.
    /// </summary>
    public static StubValueSymbol ThatFailsToRead(string instanceName, DataTypeCategory category, string typeName) =>
        new(instanceName, category, typeName, failRead: true, neverCompletesRead: false);

    /// <summary>
    /// A member whose read never completes unless its <see cref="CancellationToken"/> is
    /// cancelled — used to pin that <see cref="PlcValueDecoder"/>'s member reads are genuinely
    /// cancellable/bounded (Finding 1: the old synchronous <c>ReadValue()</c> could not be
    /// interrupted and let a slow struct block past the configured batch timeout).
    /// </summary>
    public static StubValueSymbol ThatNeverCompletesRead(string instanceName, DataTypeCategory category, string typeName) =>
        new(instanceName, category, typeName, failRead: false, neverCompletesRead: true);

    private readonly bool _allowSynchronousRead;

    /// <summary>
    /// A symbol whose synchronous <see cref="ReadValue()"/> succeeds, returning
    /// <paramref name="readValue"/>. Opt-in, because exactly ONE caller in the library may legally
    /// use it: <c>AdsConnection.GetNotificationValue</c>'s fallback for a symbol whose notification
    /// payload cannot serve. Every other stub keeps <see cref="ReadValue()"/> throwing, so a
    /// regression that reintroduces a synchronous, non-cancellable read anywhere else — in
    /// <see cref="PlcValueDecoder"/> above all — still fails loudly.
    /// </summary>
    public static StubValueSymbol WithSynchronousReadValue(string instanceName,
        DataTypeCategory category, string typeName, object readValue) =>
        new(instanceName, category, typeName, readValue, allowSynchronousRead: true);

    private StubValueSymbol(string instanceName, DataTypeCategory category, string typeName,
        object readValue, bool allowSynchronousRead)
        : base(category, typeName)
    {
        InstanceName = instanceName;

        // Both reads report the same value, so a test never has to care which one served it —
        // only how many times the synchronous one did.
        _value = readValue;
        _allowSynchronousRead = allowSynchronousRead;
    }

    /// <summary>
    /// Counts the synchronous reads this symbol served — how a test asserts POSITIVELY that the
    /// notification round-trip was avoided (0) rather than merely that some value arrived.
    /// </summary>
    public int SynchronousReadCount { get; private set; }

    // ReadValue() (the synchronous, non-cancellable overload) is not called by PlcValueDecoder —
    // it reads struct members / array elements via ReadValueAsync instead (see PlcValueDecoder's
    // remarks on being "async and cancellable, all the way down"). Per this stub's strict policy
    // (throw for anything not genuinely read) it therefore throws unless a test opted in through
    // WithSynchronousReadValue.
    public object ReadValue()
    {
        if (!_allowSynchronousRead)
            throw new NotSupportedException();

        SynchronousReadCount++;
        return _value!;
    }

    private readonly TaskCompletionSource _readCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when this member's read observes cancellation. Only meaningful for a
    /// <see cref="ThatNeverCompletesRead"/> member, whose read ends no other way. Lets a test
    /// assert POSITIVELY that a caller's token reached the member read — without it, "the read was
    /// aborted" and "the read is still hanging" look identical from outside.
    /// </summary>
    public Task ReadCancelled => _readCancelled.Task;

    // PlcValueDecoder.ReadMemberAsync calls this for every struct member / array element; a
    // successful ResultReadValueAccess wraps the constructor-provided value (errorCode 0 ==
    // AdsErrorCode.NoError). This is the one genuinely-real member added for Task 4's fix round.
    public async Task<ResultReadValueAccess> ReadValueAsync(CancellationToken cancellationToken)
    {
        if (_neverCompletesRead)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _readCancelled.TrySetResult();
                throw;
            }
        }

        if (_failRead)
            return new ResultReadValueAccess((int)AdsErrorCode.DeviceError, invokeId: 0);

        return new ResultReadValueAccess(_value!, (int)AdsErrorCode.NoError, invokeId: 0);
    }

    // --- Not consumed by PlcValueDecoder -------------------------------------
    public event EventHandler<ValueChangedEventArgs>? ValueChanged
    {
        add => throw new NotSupportedException();
        remove => throw new NotSupportedException();
    }

    public event EventHandler<RawValueChangedEventArgs>? RawValueChanged
    {
        add => throw new NotSupportedException();
        remove => throw new NotSupportedException();
    }

    public SymbolAccessRights AccessRights => throw new NotSupportedException();
    public IConnection Connection => throw new NotSupportedException();

    public INotificationSettings? NotificationSettings
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public bool HasValue => throw new NotSupportedException();

    // Genuinely read since Task 10: NotificationPayload.TryDecodeValue reaches the symbol's own
    // value factory through this. Null (the default) is a real production value — Beckhoff's
    // Symbol.ValueAccessor returns null when its factory services carry no accessor — and makes the
    // payload decode decline, so a test needs to set this only to exercise the decode itself.
    public IAccessorRawValue? ValueAccessor { get; set; }

    public void WriteValue(object value) => throw new NotSupportedException();
    public void WriteValue(object value, int size) => throw new NotSupportedException();
    public Task<ResultWriteAccess> WriteValueAsync(object value, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public ResultWriteAccess WriteValueAsResult(object value) => throw new NotSupportedException();
    public int TryWriteValue(object value, int size) => throw new NotSupportedException();

    public object ReadValue(int size) => throw new NotSupportedException();
    public ResultReadValueAccess ReadValueAsResult() => throw new NotSupportedException();
    public int TryReadValue(int size, out object value) => throw new NotSupportedException();

    public byte[] ReadRawValue() => throw new NotSupportedException();
    public byte[] ReadRawValue(int size) => throw new NotSupportedException();
    public Task<ResultReadRawAccess> ReadRawValueAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public void WriteRawValue(byte[] value) => throw new NotSupportedException();
    public void WriteRawValue(byte[] value, int size) => throw new NotSupportedException();
    public Task<ResultWriteAccess> WriteRawValueAsync(byte[] value, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void SetParent(ISymbol parent) => throw new NotSupportedException();
}

internal sealed class StubSymbolCollection(IReadOnlyList<ISymbol> symbols) : ISymbolCollection<ISymbol>
{
    public int Count => symbols.Count;

    public IEnumerator<ISymbol> GetEnumerator() => symbols.GetEnumerator();

    // --- Not consumed by PlcValueDecoder -------------------------------------
    // foreach binds to the generic GetEnumerator() above via IEnumerable<ISymbol>; this
    // non-generic overload is never invoked by the decoder or any test.
    IEnumerator IEnumerable.GetEnumerator() => throw new NotSupportedException();

    // Genuinely read since Task 6: AdsConnection.MapSymbol recurses via
    // `symbol.SubSymbols.Select(...).ToList()`. Because this collection is (transitively) an
    // IList<ISymbol>, .NET's LINQ Select specializes to an indexer-based fast path
    // (IListSelectIterator) instead of calling GetEnumerator — confirmed empirically, and the
    // same reason FakeSymbolCollection's indexer getter had to become real. The setter remains
    // unused (nothing ever assigns into a symbol collection) and stays throwing.
    public ISymbol this[int index]
    {
        get => symbols[index];
        set => throw new NotSupportedException();
    }

    public ISymbol this[string name] => throw new NotSupportedException();
    public bool IsReadOnly => throw new NotSupportedException();
    public InstanceCollectionMode Mode => throw new NotSupportedException();

    public int IndexOf(ISymbol item) => throw new NotSupportedException();
    public void Insert(int index, ISymbol item) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void Add(ISymbol item) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(ISymbol item) => throw new NotSupportedException();
    public bool Contains(string name) => throw new NotSupportedException();
    public bool ContainsName(string name) => throw new NotSupportedException();
    public void CopyTo(ISymbol[] array, int arrayIndex) => throw new NotSupportedException();
    public bool Remove(ISymbol item) => throw new NotSupportedException();
    public bool TryGetInstance(string instancePath, out ISymbol value) => throw new NotSupportedException();
    public bool TryGetInstanceByName(string name, out IList<ISymbol> value) => throw new NotSupportedException();
    public ISymbol GetInstance(string instancePath) => throw new NotSupportedException();
    public IList<ISymbol> GetInstanceByName(string name) => throw new NotSupportedException();
}
