using System.Collections;
using System.Text;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.TypeSystem;
using TwinCAT.ValueAccess;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Minimal <see cref="ISymbol"/> stub exposing what <see cref="PlcValueDecoder"/> and the
/// batch-read partition in <c>AdsConnection.ReadValuesAsync</c> genuinely read:
/// <see cref="Category"/>, <see cref="TypeName"/>, <see cref="InstanceName"/> and
/// <see cref="SubSymbols"/> (its <c>Count</c> and enumeration), plus (on
/// <see cref="StubValueSymbol"/>) the async <c>ReadValueAsync(CancellationToken)</c>
/// <see cref="PlcValueDecoder"/> now uses to read struct members / array elements. Everything
/// else throws so that any new dependency either consumer grows fails loudly rather than
/// silently passing on a default.
/// </summary>
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

    // --- Not consumed by PlcValueDecoder -------------------------------------
    public string InstancePath => throw new NotSupportedException();
    public int ByteSize => throw new NotSupportedException();
    public string Comment => throw new NotSupportedException();
    public IDataType? DataType => throw new NotSupportedException();
    public ISymbol? Parent => throw new NotSupportedException();
    public bool IsContainerType => throw new NotSupportedException();
    public bool IsPrimitiveType => throw new NotSupportedException();
    public bool IsPersistent => throw new NotSupportedException();
    public bool IsReadOnly => throw new NotSupportedException();
    public bool IsRecursive => throw new NotSupportedException();
    public bool IsReference => throw new NotSupportedException();
    public bool IsPointer => throw new NotSupportedException();
    public bool IsBitType => throw new NotSupportedException();
    public bool IsStatic => throw new NotSupportedException();
    public bool IsProperty => throw new NotSupportedException();
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

    // ReadValue() (the synchronous, non-cancellable overload) is no longer called by
    // PlcValueDecoder — it reads struct members / array elements via ReadValueAsync instead
    // (see PlcValueDecoder's remarks on being "async and cancellable, all the way down"), so
    // per this stub's strict policy (throw for anything not genuinely read), this now throws.
    public object ReadValue() => throw new NotSupportedException();

    // PlcValueDecoder.ReadMemberAsync calls this for every struct member / array element; a
    // successful ResultReadValueAccess wraps the constructor-provided value (errorCode 0 ==
    // AdsErrorCode.NoError). This is the one genuinely-real member added for Task 4's fix round.
    public async Task<ResultReadValueAccess> ReadValueAsync(CancellationToken cancellationToken)
    {
        if (_neverCompletesRead)
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

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
    public IAccessorRawValue ValueAccessor => throw new NotSupportedException();

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

    public ISymbol this[int index]
    {
        get => throw new NotSupportedException();
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
