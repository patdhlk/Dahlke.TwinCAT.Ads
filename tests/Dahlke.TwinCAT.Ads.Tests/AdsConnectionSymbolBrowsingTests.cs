using System.Collections;
using System.Text;
using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// The two-argument overload is documented as equivalent to <c>includeChildren: true</c>, which
    /// is what the recursive test above pins. This pins the other half: asking for one level
    /// returns one level, so a root browse does not project the whole PLC.
    /// </summary>
    [Fact]
    public async Task GetSymbolsAsync_IncludeChildrenFalse_ReturnsOneLevel_WithNullChildren()
    {
        var speed = new StubValueSymbol("Speed", DataTypeCategory.Primitive, "INT", 1500);
        var running = new StubValueSymbol("Running", DataTypeCategory.Primitive, "BOOL", true);
        var motor = new StubSymbol(DataTypeCategory.Struct, "ST_Motor", speed, running) { InstanceName = "MAIN.Motor" };
        var main = new StubSymbol(DataTypeCategory.Struct, "MAIN_PRG", motor) { InstanceName = "MAIN" };
        using var connection = CreateConnection(30_000, main);

        var symbols = await connection.GetSymbolsAsync(null, includeChildren: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var mainInfo = Assert.Single(symbols);
        Assert.Equal("MAIN", mainInfo.InstancePath);

        // Null, never an empty list — the same distinction the leaf case makes.
        Assert.Null(mainInfo.Children);
    }

    [Fact]
    public async Task GetSymbolsAsync_IncludeChildrenTrue_MatchesTheTwoArgumentOverload()
    {
        var speed = new StubValueSymbol("Speed", DataTypeCategory.Primitive, "INT", 1500);
        var motor = new StubSymbol(DataTypeCategory.Struct, "ST_Motor", speed) { InstanceName = "MAIN.Motor" };
        var main = new StubSymbol(DataTypeCategory.Struct, "MAIN_PRG", motor) { InstanceName = "MAIN" };
        using var connection = CreateConnection(30_000, main);

        var explicitly = await connection.GetSymbolsAsync(null, includeChildren: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var byDefault = await connection.GetSymbolsAsync(null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(byDefault.Count, explicitly.Count);
        Assert.Equal(
            byDefault[0].Children!.Single().InstancePath,
            explicitly[0].Children!.Single().InstancePath);
        Assert.Equal(
            byDefault[0].Children!.Single().Children!.Count,
            explicitly[0].Children!.Single().Children!.Count);
    }

    // ------------------------------------------------------------------
    // Walk guards: depth cap and Beckhoff's own recursion flag.
    //
    // The walk is unbounded by construction — SubSymbols is followed until it
    // runs out. Combined with the abandon-on-timeout design, a runaway walk is
    // never stopped: it keeps allocating on a thread-pool thread after the
    // caller has given up, and every retry starts another one. These pin the
    // two cheap guards against that.
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetSymbolsAsync_DoesNotDescendIntoASymbolFlaggedRecursive()
    {
        var leaf = new StubValueSymbol("Leaf", DataTypeCategory.Primitive, "INT", 1);
        var node = new StubSymbol(DataTypeCategory.Struct, "ST_Node", leaf)
        {
            InstanceName = "MAIN.Node",
            IsRecursive = true,
        };
        var main = new StubSymbol(DataTypeCategory.Struct, "MAIN_PRG", node) { InstanceName = "MAIN" };
        using var connection = CreateConnection(30_000, main);

        var symbols = await connection.GetSymbolsAsync(null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        // MAIN itself is walked; the recursive node below it becomes a leaf.
        var mainInfo = Assert.Single(symbols);
        var nodeInfo = Assert.Single(mainInfo.Children!);
        Assert.Equal("MAIN.Node", nodeInfo.InstancePath);
        Assert.Null(nodeInfo.Children);
    }

    [Fact]
    public async Task GetSymbolsAsync_StopsAtTheDepthCap()
    {
        // One chain deeper than the cap allows, so the walk must stop on its own.
        var root = BuildChain(AdsConnection.MaxSymbolWalkDepth + 5);
        using var connection = CreateConnection(30_000, root);

        var symbols = await connection.GetSymbolsAsync(null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var depth = 0;
        var cursor = Assert.Single(symbols);
        while (cursor.Children is { Count: > 0 })
        {
            depth++;
            cursor = cursor.Children[0];
        }

        Assert.Equal(AdsConnection.MaxSymbolWalkDepth, depth);
    }

    [Fact]
    public async Task SearchSymbolsAsync_StopsAtTheDepthCap()
    {
        var root = BuildChain(AdsConnection.MaxSymbolWalkDepth + 5);
        using var connection = CreateConnection(30_000, root);

        var matches = await connection.SearchSymbolsAsync("Level", includeChildren: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        // The flattening walk is capped too, so it cannot enumerate past the ceiling.
        Assert.True(
            matches.Count <= AdsConnection.MaxSymbolWalkDepth + 1,
            $"Flatten walked {matches.Count} symbols, past the cap of {AdsConnection.MaxSymbolWalkDepth}.");
    }

    [Fact]
    public async Task SearchSymbolsAsync_DoesNotDescendIntoASymbolFlaggedRecursive()
    {
        var leaf = new StubValueSymbol("Level_Leaf", DataTypeCategory.Primitive, "INT", 1);
        var node = new StubSymbol(DataTypeCategory.Struct, "ST_Node", leaf)
        {
            InstanceName = "Level_Node",
            IsRecursive = true,
        };
        using var connection = CreateConnection(30_000, node);

        var matches = await connection.SearchSymbolsAsync("Level", includeChildren: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        // The recursive node matches; the leaf beneath it is never reached.
        var match = Assert.Single(matches);
        Assert.Equal("Level_Node", match.InstancePath);
    }

    /// <summary>Builds a single chain of nested struct symbols <paramref name="length"/> deep.</summary>
    private static StubSymbol BuildChain(int length)
    {
        var current = new StubSymbol(DataTypeCategory.Primitive, "INT") { InstanceName = $"Level{length}" };
        for (var i = length - 1; i >= 0; i--)
            current = new StubSymbol(DataTypeCategory.Struct, "ST_Level", current) { InstanceName = $"Level{i}" };

        return current;
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
        // The browse (a blocking enumeration) is held on a gate rather than a delay; the browse
        // timeout is 50ms. The caller must stop waiting at ~50ms while the browse is still blocked
        // on the gate — proving the timeout actually bounds the CALLER's wait even though the
        // underlying browse cannot itself be interrupted (see RunBrowseAsync's remarks: it races
        // the browse against a timer via Task.WhenAny rather than relying on Task.Run's
        // CancellationToken, which does nothing once the delegate has already started).
        var options = new PlcTargetOptions { DisplayName = "PLC 1", TimeoutMs = 3000, SymbolBrowseTimeoutMs = 50 };
        using var connection = new AdsConnection("plc1", options, new NullLoggerFactory());
        using var browseGate = new ManualResetEventSlim(initialState: false);
        var loader = new SlowIterationSymbolLoader(TimeSpan.Zero, gate: browseGate);
        connection.SetSymbolLoaderForTesting(loader);

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => connection.SearchSymbolsAsync("anything", includeChildren: false, CancellationToken.None));

            // The invariant is ordering, not duration: the caller must have given up while the browse
            // was still running. Asserting on elapsed milliseconds instead would be flaky, because the
            // fake blocks a thread-pool thread until released and a small CI runner can starve the
            // timeout continuation long past the 50ms budget without the mechanism being wrong.
            // Ordering alone is not sufficient, though: without the gate, a starved timeout
            // continuation would let the browse finish first and win Task.WhenAny outright, so
            // Assert.ThrowsAsync<TimeoutException> would fail with "no exception thrown" before this
            // ordering assertion is ever reached. The gate is what guarantees ThrowsAsync itself
            // cannot lose that race — the browse cannot complete until the gate is released below.
            Assert.False(loader.BrowseCompleted,
                "Expected the caller to stop waiting while the slow browse was still running, but the browse had already completed.");
        }
        finally
        {
            // Release the blocked thread-pool thread rather than leaving it on its 30s internal
            // wait — leaking that under a constrained-CPU test run would itself cause flakes
            // elsewhere.
            browseGate.Set();
        }
    }

    [Fact]
    public async Task SearchSymbolsAsync_CallerCancels_ThrowsOperationCanceledException_NotTimeoutException()
    {
        // A large SymbolBrowseTimeoutMs so only the caller's own cancellation can win the race —
        // still true, but no longer the whole mechanism. The gate is what makes this deterministic:
        // the browse is held on a gate rather than a delay, so the enumeration cannot finish until
        // the test releases it below, and a starved CI runner cannot let the browse complete first
        // and change which exception the caller observes.
        var options = new PlcTargetOptions { DisplayName = "PLC 1", TimeoutMs = 3000, SymbolBrowseTimeoutMs = 60_000 };
        using var connection = new AdsConnection("plc1", options, new NullLoggerFactory());
        using var browseGate = new ManualResetEventSlim(initialState: false);
        var loader = new SlowIterationSymbolLoader(TimeSpan.Zero, gate: browseGate);
        connection.SetSymbolLoaderForTesting(loader);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => connection.SearchSymbolsAsync("anything", includeChildren: false, cts.Token));

            Assert.False(loader.BrowseCompleted,
                "Expected the caller's cancellation to win while the gated browse was still running.");
        }
        finally
        {
            // Release the blocked thread-pool thread rather than leaving it on its 30s internal
            // wait — leaking that under a constrained-CPU test run would itself cause flakes
            // elsewhere.
            browseGate.Set();
        }
    }

    [Fact]
    public async Task SearchSymbolsAsync_AbandonedBrowse_ThatLaterFaults_IsLoggedAsWarning_NotUnobserved()
    {
        // The browse itself blocks for 300ms then throws — but the browse timeout is 50ms, so the
        // caller has already stopped waiting (TimeoutException) by the time the browse actually
        // fails. Finding 1: that fault must still be OBSERVED (never an unobserved task exception
        // at finalization — a real risk on a host with ThrowUnobservedTaskExceptions enabled) and
        // LOGGED at Warning, since the caller never sees the browse's own exception.
        var capturing = new CapturingLoggerFactory();
        var options = new PlcTargetOptions { DisplayName = "PLC 1", TimeoutMs = 3000, SymbolBrowseTimeoutMs = 50 };
        using var connection = new AdsConnection("plc1", options, capturing);
        var browseFailure = new InvalidOperationException("simulated ADS upload failure");
        using var browseGate = new ManualResetEventSlim(initialState: false);
        connection.SetSymbolLoaderForTesting(
            new SlowIterationSymbolLoader(TimeSpan.Zero, browseFailure, browseGate));

        // The browse blocks on the gate, so it cannot possibly complete before the 50ms browse
        // timeout — the caller deterministically observes TimeoutException rather than the browse's
        // own exception, no matter how loaded the machine is.
        await Assert.ThrowsAsync<TimeoutException>(
            () => connection.SearchSymbolsAsync("anything", includeChildren: false, CancellationToken.None));

        // Now let the abandoned browse run on and fail. Its fault must still be observed and logged.
        browseGate.Set();
        var logged = await WaitForConditionAsync(
            () => capturing.Entries.Any(e => e.Message.Contains("Abandoned symbol browse")),
            TimeSpan.FromSeconds(3));
        Assert.True(logged, "Expected the abandoned browse's eventual fault to be logged.");

        var entry = Assert.Single(capturing.Entries, e => e.Message.Contains("Abandoned symbol browse"));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(browseFailure, entry.Exception);
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
        return predicate();
    }

    /// <summary>
    /// Minimal capturing <see cref="ILoggerFactory"/>, local to this test class (the same shape as
    /// the one already used privately in <c>PoolDeferredStartTests</c>, plus the log level since
    /// this test needs to assert Warning specifically). Records every log call's level, message,
    /// and exception so a test can assert on it without a mocking framework.
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

    /// <summary>
    /// A one-off <see cref="IDynamicSymbolLoader"/> double, local to this test class, whose root
    /// symbol collection blocks synchronously in <c>GetEnumerator</c> for a caller-supplied delay
    /// before either yielding an empty tree or throwing <paramref name="throwAfterDelay"/> (used
    /// to simulate an abandoned browse that goes on to fail — Finding 1). Used only to pin
    /// <c>RunBrowseAsync</c>'s timeout/cancellation race and fault-observation behaviour
    /// deterministically, simulating a slow/failing PLC symbol upload without hardware or a real
    /// multi-second wait. Reuses the same <c>SetSymbolLoaderForTesting</c> seam as
    /// <see cref="FakeDynamicSymbolLoader"/> — this is a second test double, not a second seam.
    /// Every member besides the one genuinely exercised (<c>GetEnumerator</c> on the root
    /// collection, reached via <c>AdsConnection.FlattenSymbols</c>'s plain <c>foreach</c>) throws,
    /// per this repo's stub-integrity policy.
    /// </summary>
    private sealed class SlowIterationSymbolLoader(
        TimeSpan delay, Exception? throwAfterDelay = null, ManualResetEventSlim? gate = null) : IDynamicSymbolLoader
    {
        private readonly SlowSymbolCollection _symbols = new(delay, throwAfterDelay, gate);

        public ISymbolCollection<ISymbol> Symbols => _symbols;

        /// <summary>
        /// Whether the blocking enumeration has run to completion. Lets a test assert that the
        /// caller stopped waiting <b>before</b> the browse finished, which is the actual invariant —
        /// rather than inferring it from elapsed wall-clock, which is unreliable on a loaded CI
        /// runner because the fake blocks a thread-pool thread for the whole delay.
        /// </summary>
        public bool BrowseCompleted => _symbols.Completed;

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

        private sealed class SlowSymbolCollection(
            TimeSpan delay, Exception? throwAfterDelay, ManualResetEventSlim? gate) : ISymbolCollection<ISymbol>
        {
            private volatile bool _completed;

            public bool Completed => _completed;

            public IEnumerator<ISymbol> GetEnumerator()
            {
                // A gate makes the browse's duration explicit rather than racing a timer: the
                // enumeration cannot finish until the test releases it, so a starved CI runner
                // cannot let the browse beat the browse-timeout and change which exception the
                // caller observes.
                if (gate is not null)
                    gate.Wait(TimeSpan.FromSeconds(30));
                else
                    Thread.Sleep(delay);
                _completed = true;
                if (throwAfterDelay is not null)
                    throw throwAfterDelay;
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
