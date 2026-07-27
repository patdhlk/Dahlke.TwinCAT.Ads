using System.Collections;
using System.Text;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.TypeSystem;
using TwinCAT.ValueAccess;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Minimal <see cref="ISymbol"/> stub exposing only what <see cref="PlcValueDecoder"/>
/// consumes: <see cref="Category"/>, <see cref="TypeName"/>, <see cref="InstanceName"/>
/// and <see cref="SubSymbols"/>. Everything else throws so that any new dependency the
/// decoder grows fails loudly rather than silently returning a default.
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
    public string InstancePath => InstanceName;
    public ISymbolCollection<ISymbol> SubSymbols { get; }
    public int ByteSize => 0;
    public string Comment => string.Empty;

    // --- Not consumed by PlcValueDecoder -------------------------------------
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

    public StubValueSymbol(string instanceName, DataTypeCategory category, string typeName,
        object? value, params ISymbol[] subSymbols)
        : base(category, typeName, subSymbols)
    {
        InstanceName = instanceName;
        _value = value;
    }

    public object ReadValue() => _value!;

    // --- Not consumed by PlcValueDecoder -------------------------------------
#pragma warning disable CS0067 // Never raised — this stub has no notification lifecycle.
    public event EventHandler<ValueChangedEventArgs>? ValueChanged;
    public event EventHandler<RawValueChangedEventArgs>? RawValueChanged;
#pragma warning restore CS0067
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
    public Task<ResultReadValueAccess> ReadValueAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();
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
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // --- Not consumed by PlcValueDecoder -------------------------------------
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
