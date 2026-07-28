using System.Collections;
using System.Diagnostics;
using System.Text;
using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.TypeSystem;
using TwinCAT.TypeSystem.Generic;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Pins <c>AdsConnection.GetSymbolsAsync</c>/<c>SearchSymbolsAsync</c> end to end, without
/// hardware, via the internal <c>SetSymbolLoaderForTesting</c> seam — the same seam and reasoning
/// as <see cref="ReadValueWithMetadataAsyncTests"/>.
/// </summary>
/// <remarks>
/// The shared <c>AdsConnectionContractTests</c> suite pins the browsing CONTRACT against the
/// simulated and facade harnesses, but <see cref="AdsConnection"/> itself is not among the
/// contract-suite derivations (it needs a live/faked symbol loader, not just a store). This class
/// fills that gap, and is the only place the thread-pool/timeout-race design in
/// <c>AdsConnection.RunBrowseAsync</c> is exercised at all — a real PLC symbol upload can
/// genuinely run past <see cref="PlcTargetOptions.SymbolBrowseTimeoutMs"/>, and there is no other
/// way to simulate that deterministically without hardware.
/// </remarks>
public class AdsConnectionSymbolBrowsingTests
{
    private static AdsConnection CreateConnection(int symbolBrowseTimeoutMs, params ISymbol[] symbols)
    {
        var options = new PlcTargetOptions { DisplayName = "PLC 1", TimeoutMs = 3000, SymbolBrowseTimeoutMs = symbolBrowseTimeoutMs };
        var connection = new AdsConnection("plc1", options, new NullLoggerFactory());
        connection.SetSymbolLoaderForTesting(new FakeDynamicSymbolLoader(symbols));
        return connection;
    }

    [Fact]
    public async Task GetSymbolsAsync_NullParent_ReturnsRootSymbols_WithMappedMetadata()
    {
        var speed = new StubValueSymbol("MAIN.Speed", DataTypeCategory.Primitive, "INT", 1500)
        {
            ByteSize = 2,
            Comment = "motor speed",
        };
        using var connection = CreateConnection(30_000, speed);

        var symbols = await connection.GetSymbolsAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var info = Assert.Single(symbols);
        Assert.Equal("MAIN.Speed", info.InstancePath);
        Assert.Equal("INT", info.TypeName);
        Assert.Equal("Primitive", info.Category);
        Assert.Equal(2, info.ByteSize);
        Assert.Equal("motor speed", info.Comment);
        Assert.Null(info.Children);
    }

    [Fact]
    public async Task GetSymbolsAsync_EmptyComment_MapsToNullNotEmptyString()
    {
        var speed = new StubValueSymbol("MAIN.Speed", DataTypeCategory.Primitive, "INT", 1500);
        using var connection = CreateConnection(30_000, speed);

        var symbols = await connection.GetSymbolsAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(Assert.Single(symbols).Comment);
    }

    [Fact]
    public async Task GetSymbolsAsync_KnownParent_ReturnsOnlyItsSubSymbols()
    {
        var speed = new StubValueSymbol("MAIN.Speed", DataTypeCategory.Primitive, "INT", 1500);
        var running = new StubValueSymbol("MAIN.Running", DataTypeCategory.Primitive, "BOOL", true);
        var main = new StubSymbol(DataTypeCategory.Struct, "MAIN_PRG", speed, running) { InstanceName = "MAIN" };
        using var connection = CreateConnection(30_000, main);

        var symbols = await connection.GetSymbolsAsync("MAIN", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, symbols.Count);
        Assert.Contains(symbols, s => s.InstancePath == "MAIN.Speed");
        Assert.Contains(symbols, s => s.InstancePath == "MAIN.Running");
    }

    [Fact]
    public async Task GetSymbolsAsync_LeafSymbol_HasNullChildren_NotEmptyList()
    {
        var speed = new StubValueSymbol("MAIN.Speed", DataTypeCategory.Primitive, "INT", 1500);
        using var connection = CreateConnection(30_000, speed);

        var symbols = await connection.GetSymbolsAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(Assert.Single(symbols).Children);
    }

    [Fact]
    public async Task GetSymbolsAsync_IncludesNestedChildren_Recursively()
    {
        var speed = new StubValueSymbol("Speed", DataTypeCategory.Primitive, "INT", 1500);
        var running = new StubValueSymbol("Running", DataTypeCategory.Primitive, "BOOL", true);
        var motor = new StubSymbol(DataTypeCategory.Struct, "ST_Motor", speed, running) { InstanceName = "MAIN.Motor" };
        var main = new StubSymbol(DataTypeCategory.Struct, "MAIN_PRG", motor) { InstanceName = "MAIN" };
        using var connection = CreateConnection(30_000, main);

        var symbols = await connection.GetSymbolsAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var mainInfo = Assert.Single(symbols);
        Assert.Equal("MAIN", mainInfo.InstancePath);
        var motorInfo = Assert.Single(mainInfo.Children!);
        Assert.Equal("MAIN.Motor", motorInfo.InstancePath);
        Assert.Equal(2, motorInfo.Children!.Count);
        Assert.All(motorInfo.Children!, c => Assert.Null(c.Children));
    }

    [Fact]
    public async Task GetSymbolsAsync_UnknownParent_ThrowsAdsErrorException_SymbolNotFound()
    {
        using var connection = CreateConnection(30_000); // no symbols registered

        var ex = await Assert.ThrowsAsync<AdsErrorException>(
            () => connection.GetSymbolsAsync("MAIN.NoSuchParent", CancellationToken.None));

        Assert.Equal(AdsErrorCode.DeviceSymbolNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task GetSymbolsAsync_PreCancelledToken_ThrowsOperationCanceledException_Immediately()
    {
        using var connection = CreateConnection(30_000);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => connection.GetSymbolsAsync(null, cts.Token));
    }

    [Fact]
    public async Task SearchSymbolsAsync_WalksWholeTree_MatchesCaseInsensitively()
    {
        // StubSymbol.InstancePath mirrors InstanceName directly (no hierarchy composition), so
        // each level's full dotted path must be set explicitly.
        var speed = new StubValueSymbol("MAIN.Motor.Speed", DataTypeCategory.Primitive, "INT", 1500);
        var motor = new StubSymbol(DataTypeCategory.Struct, "ST_Motor", speed) { InstanceName = "MAIN.Motor" };
        var main = new StubSymbol(DataTypeCategory.Struct, "MAIN_PRG", motor) { InstanceName = "MAIN" };
        using var connection = CreateConnection(30_000, main);

        var matches = await connection.SearchSymbolsAsync("speed", includeChildren: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var match = Assert.Single(matches);
        Assert.Equal("MAIN.Motor.Speed", match.InstancePath);
    }

    [Fact]
    public async Task SearchSymbolsAsync_ReturnsEmpty_WhenNothingMatches()
    {
        var speed = new StubValueSymbol("MAIN.Speed", DataTypeCategory.Primitive, "INT", 1500);
        using var connection = CreateConnection(30_000, speed);

        var matches = await connection.SearchSymbolsAsync("zzz-no-match", includeChildren: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(matches);
    }

    [Fact]
    public async Task SearchSymbolsAsync_IncludeChildrenFalse_YieldsNullChildren_EvenForContainerMatches()
    {
        var speed = new StubValueSymbol("Speed", DataTypeCategory.Primitive, "INT", 1500);
        var main = new StubSymbol(DataTypeCategory.Struct, "MAIN_PRG", speed) { InstanceName = "MAIN" };
        using var connection = CreateConnection(30_000, main);

        // "MAIN" itself is a container (has SubSymbols) but includeChildren is false.
        var matches = await connection.SearchSymbolsAsync("MAIN", includeChildren: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEmpty(matches);
        Assert.All(matches, s => Assert.Null(s.Children));
    }

    [Fact]
    public async Task SearchSymbolsAsync_IncludeChildrenTrue_PopulatesChildren_ForContainerMatches()
    {
        var speed = new StubValueSymbol("Speed", DataTypeCategory.Primitive, "INT", 1500);
        var main = new StubSymbol(DataTypeCategory.Struct, "MAIN_PRG", speed) { InstanceName = "MAIN" };
        using var connection = CreateConnection(30_000, main);

        var matches = await connection.SearchSymbolsAsync("MAIN", includeChildren: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var mainMatch = Assert.Single(matches, s => s.InstancePath == "MAIN");
        Assert.NotNull(mainMatch.Children);
        Assert.Single(mainMatch.Children!);
    }

    // =====================================================================
    // Thread-pool/timeout-race behaviour (RunBrowseAsync). Driven through SearchSymbolsAsync
    // (its plain foreach over the root collection is the natural hook for a slow
    // GetEnumerator — see SlowIterationSymbolLoader), but RunBrowseAsync is shared code: the
    // same race governs GetSymbolsAsync too. These are the only tests in the repo that exercise
    // the abandon-on-timeout design documented on IAdsConnection.GetSymbolsAsync/SearchSymbolsAsync.
    // =====================================================================

    [Fact]
    public async Task SearchSymbolsAsync_AbandonsSlowBrowse_ThrowsTimeoutException_WhenSymbolBrowseTimeoutMsElapses()
    {
        // The browse (a blocking enumeration) would take 1s; the browse timeout is 50ms. The
        // caller must stop waiting at ~50ms, NOT ~1s — proving the timeout actually bounds the
        // CALLER's wait even though the underlying browse cannot itself be interrupted (see
        // RunBrowseAsync's remarks: it races the browse against a timer via Task.WhenAny rather
        // than relying on Task.Run's CancellationToken, which does nothing once the delegate has
        // already started).
        var options = new PlcTargetOptions { DisplayName = "PLC 1", TimeoutMs = 3000, SymbolBrowseTimeoutMs = 50 };
        using var connection = new AdsConnection("plc1", options, new NullLoggerFactory());
        connection.SetSymbolLoaderForTesting(new SlowIterationSymbolLoader(TimeSpan.FromSeconds(1)));

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(
            () => connection.SearchSymbolsAsync("anything", includeChildren: false, CancellationToken.None));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 800,
            $"Expected the caller to stop waiting well before the 1s slow browse completed; took {sw.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task SearchSymbolsAsync_CallerCancels_ThrowsOperationCanceledException_NotTimeoutException()
    {
        // A large SymbolBrowseTimeoutMs so only the caller's own cancellation can win the race.
        var options = new PlcTargetOptions { DisplayName = "PLC 1", TimeoutMs = 3000, SymbolBrowseTimeoutMs = 60_000 };
        using var connection = new AdsConnection("plc1", options, new NullLoggerFactory());
        connection.SetSymbolLoaderForTesting(new SlowIterationSymbolLoader(TimeSpan.FromSeconds(1)));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => connection.SearchSymbolsAsync("anything", includeChildren: false, cts.Token));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 800,
            $"Expected caller cancellation to win the race well before the 1s slow browse completed; took {sw.ElapsedMilliseconds}ms.");
    }

    /// <summary>
    /// A one-off <see cref="IDynamicSymbolLoader"/> double, local to this test class, whose root
    /// symbol collection blocks synchronously in <c>GetEnumerator</c> for a caller-supplied delay
    /// before yielding an empty tree. Used only to pin <c>RunBrowseAsync</c>'s timeout/cancellation
    /// race deterministically, simulating a slow PLC symbol upload without hardware or a real
    /// multi-second wait. Reuses the same <c>SetSymbolLoaderForTesting</c> seam as
    /// <see cref="FakeDynamicSymbolLoader"/> — this is a second test double, not a second seam.
    /// Every member besides the one genuinely exercised (<c>GetEnumerator</c> on the root
    /// collection, reached via <c>AdsConnection.FlattenSymbols</c>'s plain <c>foreach</c>) throws,
    /// per this repo's stub-integrity policy.
    /// </summary>
    private sealed class SlowIterationSymbolLoader(TimeSpan delay) : IDynamicSymbolLoader
    {
        public ISymbolCollection<ISymbol> Symbols { get; } = new SlowSymbolCollection(delay);

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

        private sealed class SlowSymbolCollection(TimeSpan delay) : ISymbolCollection<ISymbol>
        {
            public IEnumerator<ISymbol> GetEnumerator()
            {
                Thread.Sleep(delay);
                return Enumerable.Empty<ISymbol>().GetEnumerator();
            }

            // --- Not read by AdsConnection.FlattenSymbols --------------------------
            IEnumerator IEnumerable.GetEnumerator() => throw new NotSupportedException();
            public int Count => throw new NotSupportedException();
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
    }
}
