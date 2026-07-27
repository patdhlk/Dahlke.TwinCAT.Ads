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
/// <c>AdsConnection</c> only ever reads one member of the symbol loader it holds:
/// <c>symbolLoader.Symbols.TryGetInstance(path, out symbol)</c> (see every call site in
/// <c>AdsConnection.cs</c> — <c>ReadValueAsync</c>, <c>ReadValuesAsync</c>, <c>WriteValueAsync</c>,
/// <c>WriteValuesAsync</c>, <c>SubscribeAsync</c>). This fake makes exactly that one member real
/// (backed by a plain dictionary keyed by symbol path) and throws
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

    public FakeDynamicSymbolLoader(params ISymbol[] symbols)
    {
        _symbols = new FakeSymbolCollection(symbols);
    }

    public ISymbolCollection<ISymbol> Symbols => _symbols;

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
    public IDataTypeCollection<IDataType> DataTypes => throw new NotSupportedException();
    public Encoding DefaultValueEncoding => throw new NotSupportedException();
}

/// <summary>
/// The one genuinely-functional member <see cref="FakeDynamicSymbolLoader"/> needs: a
/// dictionary-backed <see cref="TryGetInstance"/> so <c>AdsConnection</c>'s symbol resolution
/// loop can look symbols up by path. Everything else throws — no test in this repo needs any
/// other member of <see cref="ISymbolCollection{T}"/> to be real here (distinct from
/// <see cref="StubSymbolCollection"/>, which is <see cref="PlcValueDecoder"/>'s own fake with a
/// different genuinely-real member set — <c>Count</c>/enumeration, not lookup).
/// </summary>
internal sealed class FakeSymbolCollection : ISymbolCollection<ISymbol>
{
    private readonly Dictionary<string, ISymbol> _byPath;

    public FakeSymbolCollection(IReadOnlyList<ISymbol> symbols)
    {
        _byPath = new Dictionary<string, ISymbol>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in symbols)
            _byPath[symbol.InstanceName] = symbol;
    }

    public bool TryGetInstance(string instancePath, out ISymbol value) => _byPath.TryGetValue(instancePath, out value!);

    // --- Not read by AdsConnection --------------------------------------------
    public int Count => throw new NotSupportedException();
    public IEnumerator<ISymbol> GetEnumerator() => throw new NotSupportedException();
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
    public bool TryGetInstanceByName(string name, out IList<ISymbol> value) => throw new NotSupportedException();
    public ISymbol GetInstance(string instancePath) => throw new NotSupportedException();
    public IList<ISymbol> GetInstanceByName(string name) => throw new NotSupportedException();
}
