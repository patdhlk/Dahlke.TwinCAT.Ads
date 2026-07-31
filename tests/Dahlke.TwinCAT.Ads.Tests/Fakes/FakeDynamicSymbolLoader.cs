using System.Collections;
using System.Text;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.TypeSystem;
using TwinCAT.TypeSystem.Generic;

namespace Dahlke.TwinCAT.Ads.Tests.Fakes;

/// <summary>
/// Minimal <see cref="IDynamicSymbolLoader"/> fake used to exercise
/// <c>AdsConnection.ReadValuesAsync</c>'s batch-read partition without a live PLC connection.
/// </summary>
/// <remarks>
/// <para>
/// Most of <c>AdsConnection</c>'s methods read only one member of the symbol loader they hold:
/// <c>symbolLoader.Symbols.TryGetInstance(path, out symbol)</c> (see every call site in
/// <c>AdsConnection.cs</c> — <c>ReadValueAsync</c>, <c>ReadValuesAsync</c>, <c>WriteValueAsync</c>,
/// <c>WriteValuesAsync</c>, <c>SubscribeAsync</c>). Since Task 6, <c>GetSymbolsAsync</c> and
/// <c>SearchSymbolsAsync</c> also enumerate/index the root collection to browse or flatten the
/// tree, so <see cref="FakeSymbolCollection"/> makes those members real too. This fake throws
/// <see cref="NotSupportedException"/> for everything else, matching this repo's stub-integrity
/// policy: a member is real only if a genuine caller reads it, so a future new dependency fails
/// loudly instead of silently returning a default.
/// </para>
/// <para>
/// Injected into an <c>AdsConnection</c> via its internal
/// <c>SetSymbolLoaderForTesting</c> seam, which bypasses the normal
/// <c>SymbolLoaderFactory.Create(_client, settings)</c> lazy-init path (that path requires a
/// live, connected <see cref="TwinCAT.Ads.AdsClient"/>).
/// </para>
/// </remarks>
internal sealed class FakeDynamicSymbolLoader : IDynamicSymbolLoader
{
    private readonly FakeSymbolCollection _symbols;
    private readonly FakeDataTypeCollection _dataTypes;

    public FakeDynamicSymbolLoader(params ISymbol[] symbols) : this(dataTypes: null, symbols)
    {
    }

    /// <summary>
    /// Seeds both the symbol tree and the data-type collection — the latter added so
    /// <c>AdsConnection.GetEnumMembersAsync</c> can be exercised end to end without hardware, the
    /// same way <paramref name="symbols"/> already lets <c>ReadValuesAsync</c> etc. be.
    /// </summary>
    public FakeDynamicSymbolLoader(IReadOnlyList<IDataType>? dataTypes, params ISymbol[] symbols)
    {
        _symbols = new FakeSymbolCollection(symbols);
        _dataTypes = new FakeDataTypeCollection(dataTypes ?? []);
    }

    public ISymbolCollection<ISymbol> Symbols => _symbols;

    // Genuinely read by AdsConnection.GetEnumMembersAsync: `symbolLoader.DataTypes.FirstOrDefault(...)`.
    public IDataTypeCollection<IDataType> DataTypes => _dataTypes;

    // --- Not read by AdsConnection --------------------------------------------
    public Task<ResultDynamicSymbols> GetDynamicSymbolsAsync(CancellationToken cancel) => throw new NotSupportedException();
    public IDynamicSymbolsEnumerable SymbolsDynamic => throw new NotSupportedException();
    public IDataTypeCollection BuildInTypes => throw new NotSupportedException();
    public ISymbolLoaderSettings Settings => throw new NotSupportedException();
    public INamespaceCollection<IDataType> Namespaces => throw new NotSupportedException();
    public string RootNamespaceName => throw new NotSupportedException();
    public INamespace<IDataType> RootNamespace => throw new NotSupportedException();
    public Task<ResultSymbols> GetSymbolsAsync(CancellationToken cancel) => throw new NotSupportedException();
    public AdsErrorCode TryGetSymbols(out ISymbolCollection<ISymbol> symbols) => throw new NotSupportedException();
    public Task<ResultDataTypes> GetDataTypesAsync(CancellationToken cancel) => throw new NotSupportedException();
    public AdsErrorCode TryGetDataTypes(out IDataTypeCollection<IDataType> dataTypes) => throw new NotSupportedException();
    public void ResetCachedSymbolicData() => throw new NotSupportedException();
    public ResultSymbols GetSymbols() => throw new NotSupportedException();
    public ResultDataTypes GetDataTypes() => throw new NotSupportedException();
    public Encoding DefaultValueEncoding => throw new NotSupportedException();
}

/// <summary>
/// The genuinely-functional member <see cref="FakeDynamicSymbolLoader.DataTypes"/> needs: a
/// list-backed <see cref="GetEnumerator"/> so <c>AdsConnection.GetEnumMembersAsync</c>'s
/// <c>symbolLoader.DataTypes.FirstOrDefault(predicate)</c> can walk it. The predicate overload of
/// <c>FirstOrDefault</c> enumerates via a plain <c>foreach</c> with no <c>IList</c>/<c>ICollection</c>
/// fast path (unlike <see cref="FakeEnumValueCollection"/> below, which backs a
/// <c>Select(...).ToArray()</c> and needs more), so only the generic enumerator is real here.
/// Everything else throws — no test in this repo needs any other member of
/// <see cref="IDataTypeCollection{T}"/> to be real.
/// </summary>
internal sealed class FakeDataTypeCollection(IReadOnlyList<IDataType> dataTypes) : IDataTypeCollection<IDataType>
{
    /// <summary>
    /// How many times <see cref="GetEnumerator"/> was called — lets a test assert POSITIVELY that
    /// a second <c>GetEnumMembersAsync</c> call for an already-cached type did NOT walk the data
    /// types again, rather than merely that it returned the right answer (which a broken cache
    /// that happens to re-resolve identically would also do).
    /// </summary>
    public int EnumerationCount { get; private set; }

    public IEnumerator<IDataType> GetEnumerator()
    {
        EnumerationCount++;
        return dataTypes.GetEnumerator();
    }

    // --- Not read by AdsConnection.GetEnumMembersAsync ------------------------
    IEnumerator IEnumerable.GetEnumerator() => throw new NotSupportedException();
    public bool ContainsType(string name) => throw new NotSupportedException();
    public bool TryGetType(string name, out IDataType value) => throw new NotSupportedException();
    public IDataType this[string name] => throw new NotSupportedException();
    public int Count => throw new NotSupportedException();
    public bool IsReadOnly => throw new NotSupportedException();
    public int IndexOf(IDataType item) => throw new NotSupportedException();
    public void Insert(int index, IDataType item) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();

    public IDataType this[int index]
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public void Add(IDataType item) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(IDataType item) => throw new NotSupportedException();
    public void CopyTo(IDataType[] array, int arrayIndex) => throw new NotSupportedException();
    public bool Remove(IDataType item) => throw new NotSupportedException();
}

/// <summary>
/// Minimal <see cref="IDataType"/> fake representing a resolved, NON-enum type (e.g. a struct) —
/// used to pin <c>AdsConnection.GetEnumMembersAsync</c>'s "not an enumeration" error path, which
/// reads both <see cref="Name"/> (to be found by the <c>FirstOrDefault</c> predicate) and
/// <see cref="Category"/> (named in the thrown message). Distinct from <c>StubDataType</c> in
/// <c>StubSymbol.cs</c>, whose <c>Name</c> deliberately throws because its one consumer
/// (<c>NotificationPayload</c>) never reads it.
/// </summary>
internal sealed class FakeNonEnumDataType(string name, DataTypeCategory category) : IDataType
{
    public string Name => name;
    public DataTypeCategory Category => category;

    // --- Not read by AdsConnection.GetEnumMembersAsync ------------------------
    public int Id => throw new NotSupportedException();
    public string Namespace => throw new NotSupportedException();
    public string FullName => throw new NotSupportedException();
    public bool IsPrimitive => throw new NotSupportedException();
    public bool IsContainer => throw new NotSupportedException();
    public bool IsPointer => throw new NotSupportedException();
    public bool IsReference => throw new NotSupportedException();
    public ITypeAttributeCollection Attributes => throw new NotSupportedException();
    public string Comment => throw new NotSupportedException();
    public int Size => throw new NotSupportedException();
    public bool IsBitType => throw new NotSupportedException();
    public int BitSize => throw new NotSupportedException();
    public int ByteSize => throw new NotSupportedException();
    public bool IsByteAligned => throw new NotSupportedException();
}

/// <summary>
/// Minimal <see cref="IEnumType"/> fake: only <see cref="Name"/> (matched by
/// <c>GetEnumMembersAsync</c>'s <c>FirstOrDefault</c> predicate) and <see cref="EnumValues"/> (the
/// only other member the success path reads) are real. <see cref="Category"/> is never read on
/// this path — the type check is a pattern match (<c>dataType is IEnumType enumType</c>), not a
/// <see cref="DataTypeCategory"/> comparison — so it, like everything else here, throws.
/// </summary>
/// <remarks>
/// Takes the already-constructed <see cref="IEnumValueCollection"/> rather than a plain member
/// list so a test can build a <see cref="FakeEnumValueCollection"/>, wire up its
/// <see cref="FakeEnumValueCollection.OnEnumerated"/> hook, and only THEN hand it to this type —
/// needed to reproduce the cache-write race deterministically (see
/// <c>AdsConnectionEnumMetadataTests.ConcurrentDisconnect_DuringResolve_DoesNotCacheStaleMembers</c>).
/// </remarks>
internal sealed class FakeEnumType(string name, IEnumValueCollection enumValues) : IEnumType
{
    public FakeEnumType(string name, IReadOnlyList<IEnumValue> values)
        : this(name, new FakeEnumValueCollection(values))
    {
    }

    public string Name => name;
    public IEnumValueCollection EnumValues => enumValues;

    // --- Not read by AdsConnection.GetEnumMembersAsync ------------------------
    public int Id => throw new NotSupportedException();
    public DataTypeCategory Category => throw new NotSupportedException();
    public string Namespace => throw new NotSupportedException();
    public string FullName => throw new NotSupportedException();
    public bool IsPrimitive => throw new NotSupportedException();
    public bool IsContainer => throw new NotSupportedException();
    public bool IsPointer => throw new NotSupportedException();
    public bool IsReference => throw new NotSupportedException();
    public ITypeAttributeCollection Attributes => throw new NotSupportedException();
    public string Comment => throw new NotSupportedException();
    public int Size => throw new NotSupportedException();
    public bool IsBitType => throw new NotSupportedException();
    public int BitSize => throw new NotSupportedException();
    public int ByteSize => throw new NotSupportedException();
    public bool IsByteAligned => throw new NotSupportedException();
    public string BaseTypeName => throw new NotSupportedException();
    public IDataType BaseType => throw new NotSupportedException();
    public IConvertible[] GetValues() => throw new NotSupportedException();
    public string[] GetNames() => throw new NotSupportedException();
    public IConvertible Parse(string name) => throw new NotSupportedException();
    public bool TryParse(string name, out IConvertible value) => throw new NotSupportedException();
    public bool TryParse(string name, out IEnumValue value) => throw new NotSupportedException();
    public bool Contains(string name) => throw new NotSupportedException();
    public string ToString(IConvertible value) => throw new NotSupportedException();
}

/// <summary>One enum member: only <see cref="Name"/> and <see cref="Value"/> are ever read.</summary>
internal sealed class FakeEnumValue(string name, object value) : IEnumValue
{
    public string Name => name;
    public object Value => value;

    // --- Not read by AdsConnection.GetEnumMembersAsync ------------------------
    // Primitive is Beckhoff-obsolete ("Use IEnumValue.Value instead") and, per this repo's policy
    // of adding no new warning suppression, is never referenced by production code — only Value is.
    public object Primitive => throw new NotSupportedException();
    public byte[] RawValue => throw new NotSupportedException();
    public Type ManagedBaseType => throw new NotSupportedException();
    public Type BaseType => throw new NotSupportedException();
    public int Size => throw new NotSupportedException();
    public ITypeAttributeCollection Attributes => throw new NotSupportedException();
    public string Comment => throw new NotSupportedException();
}

/// <summary>
/// The genuinely-functional members <c>AdsConnection.GetEnumMembersAsync</c> needs from
/// <see cref="IEnumType.EnumValues"/>: <see cref="GetEnumerator"/> AND <see cref="Count"/>.
/// </summary>
/// <remarks>
/// Production code runs <c>enumType.EnumValues.Select(...).ToArray()</c>. LINQ's
/// <c>Select</c>-then-<c>ToArray</c> fast path (<c>IIListProvider&lt;TResult&gt;.ToArray()</c>)
/// checks whether the SOURCE is an <see cref="ICollection{T}"/> and, if so, reads its
/// <see cref="Count"/> to pre-size the result array before enumerating — this collection is
/// (transitively, via <c>IEnumValueCollection</c>) an <c>ICollection&lt;IEnumValue&gt;</c>, so that
/// check succeeds and <see cref="Count"/> IS read. This is the exact same "both paths must be
/// real, not just one" lesson <c>FakeSymbolCollection</c>/<c>StubSymbolCollection</c> already
/// document for <c>Select(...).ToList()</c>'s <c>IList</c>-source fast path — confirmed
/// empirically here too.
/// </remarks>
internal sealed class FakeEnumValueCollection(IReadOnlyList<IEnumValue> values) : IEnumValueCollection
{
    /// <summary>
    /// Invoked once, from inside <see cref="GetEnumerator"/> — i.e. while
    /// <c>GetEnumMembersAsync</c> is still resolving, before it reaches its cache write. Lets a
    /// test simulate "a Disconnect/ForceDisconnect raced this exact resolve" deterministically, on
    /// one thread, with no timing dependency: the hook itself calls back into the connection under
    /// test to clear its cache mid-resolve.
    /// </summary>
    public Action? OnEnumerated { get; set; }

    public int Count => values.Count;

    public IEnumerator<IEnumValue> GetEnumerator()
    {
        OnEnumerated?.Invoke();
        return values.GetEnumerator();
    }

    // --- Not read by AdsConnection.GetEnumMembersAsync ------------------------
    IEnumerator IEnumerable.GetEnumerator() => throw new NotSupportedException();
    public bool IsReadOnly => throw new NotSupportedException();
    public void Add(IEnumValue item) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(IEnumValue item) => throw new NotSupportedException();
    public bool Contains(string name) => throw new NotSupportedException();
    public void CopyTo(IEnumValue[] array, int arrayIndex) => throw new NotSupportedException();
    public bool Remove(IEnumValue item) => throw new NotSupportedException();
    public bool TryParse(string name, out IConvertible value) => throw new NotSupportedException();
    public bool TryParse(string name, out IEnumValue value) => throw new NotSupportedException();
    public IConvertible Parse(string name) => throw new NotSupportedException();
    public string[] GetNames() => throw new NotSupportedException();
    public IConvertible[] GetValues() => throw new NotSupportedException();
    public IConvertible this[string name] => throw new NotSupportedException();
}

/// <summary>
/// The genuinely-functional members <see cref="FakeDynamicSymbolLoader"/> needs: a
/// dictionary-backed <see cref="TryGetInstance"/> so <c>AdsConnection</c>'s symbol resolution
/// loop can look symbols up by path, plus (since Task 6's symbol browsing)
/// <see cref="Count"/>, the <see cref="this[int]"/> indexer getter, and
/// <see cref="GetEnumerator"/>. <c>GetSymbolsAsync</c>/<c>SearchSymbolsAsync</c> read the root
/// collection via <c>.Select(...).ToList()</c> and plain <c>foreach</c>; LINQ's <c>Select</c>
/// special-cases any <see cref="IList{T}"/> source (which this collection is, transitively) by
/// reading <c>Count</c> and indexing rather than calling <c>GetEnumerator</c> — confirmed
/// empirically across net8.0/9.0/10.0 — so both paths must be real, not just one. Everything
/// else throws — no test in this repo needs any other member of <see cref="ISymbolCollection{T}"/>
/// to be real here (distinct from <see cref="StubSymbolCollection"/>, which is
/// <see cref="PlcValueDecoder"/>'s own fake with a different genuinely-real member set).
/// </summary>
internal sealed class FakeSymbolCollection : ISymbolCollection<ISymbol>
{
    private readonly Dictionary<string, ISymbol> _byPath;
    private readonly IReadOnlyList<ISymbol> _all;

    public FakeSymbolCollection(IReadOnlyList<ISymbol> symbols)
    {
        _all = symbols;
        _byPath = new Dictionary<string, ISymbol>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in symbols)
            _byPath[symbol.InstanceName] = symbol;
    }

    public bool TryGetInstance(string instancePath, out ISymbol value) => _byPath.TryGetValue(instancePath, out value!);
    public int Count => _all.Count;
    public IEnumerator<ISymbol> GetEnumerator() => _all.GetEnumerator();

    public ISymbol this[int index]
    {
        get => _all[index];
        set => throw new NotSupportedException();
    }

    // --- Not read by AdsConnection --------------------------------------------
    IEnumerator IEnumerable.GetEnumerator() => throw new NotSupportedException();
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
    public bool TryGetInstanceByName(string name, out IList<ISymbol> value) => throw new NotSupportedException();
    public ISymbol GetInstance(string instancePath) => throw new NotSupportedException();
    public IList<ISymbol> GetInstanceByName(string name) => throw new NotSupportedException();
}
