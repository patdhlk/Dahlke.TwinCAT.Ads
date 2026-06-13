using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Generic <see cref="IAdsConnection.ReadValueAsync{T}"/> and
/// <see cref="IAdsConnection.WriteValueAsync{T}"/> — typed read/write as the headline API.
///
/// Coverage:
/// - <see cref="SimulatedAdsConnection"/>: typed round-trips (int, bool, string, double),
///   numeric widening (int→double), string-seeded conversions ("42"→int, "true"→bool,
///   "3.14"→double with InvariantCulture), type mismatch → actionable InvalidCastException,
///   null/missing + value-type T → actionable throw, null/missing + reference T → null,
///   missing symbol typed read → throw (divergence from object? overload).
/// - <see cref="AdsConnectionFacade"/>: typed calls route via SnapshotAsync; wait-then-throw
///   applies to typed reads too.
/// - Cancellation: typed read observes CancellationToken.
/// </summary>
public class GenericReadWriteTests
{
    private static SimulatedAdsConnection CreateSim()
        => new("test-plc", "Test PLC", NullLoggerFactory.Instance);

    // =========================================================================
    // SimulatedAdsConnection — typed round-trips
    // =========================================================================

    [Fact]
    public async Task TypedRoundTrip_Int_WriteThenRead_ReturnsInt()
    {
        using var conn = CreateSim();
        await conn.WriteValueAsync<int>("MAIN.Counter", 42, CancellationToken.None);

        var result = await conn.ReadValueAsync<int>("MAIN.Counter", CancellationToken.None);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task TypedRoundTrip_Bool_WriteThenRead_ReturnsBool()
    {
        using var conn = CreateSim();
        await conn.WriteValueAsync<bool>("MAIN.Flag", true, CancellationToken.None);

        var result = await conn.ReadValueAsync<bool>("MAIN.Flag", CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task TypedRoundTrip_String_WriteThenRead_ReturnsString()
    {
        using var conn = CreateSim();
        await conn.WriteValueAsync<string>("MAIN.Name", "Hello", CancellationToken.None);

        var result = await conn.ReadValueAsync<string>("MAIN.Name", CancellationToken.None);

        Assert.Equal("Hello", result);
    }

    [Fact]
    public async Task TypedRoundTrip_Double_WriteThenRead_ReturnsDouble()
    {
        using var conn = CreateSim();
        await conn.WriteValueAsync<double>("MAIN.Pressure", 3.14, CancellationToken.None);

        var result = await conn.ReadValueAsync<double>("MAIN.Pressure", CancellationToken.None);

        Assert.Equal(3.14, result);
    }

    // =========================================================================
    // SimulatedAdsConnection — numeric widening
    // =========================================================================

    [Fact]
    public async Task NumericWidening_StoredInt_ReadAsDouble_Succeeds()
    {
        using var conn = CreateSim();
        // PLC reality: a symbol may be seeded as int but read as double by a consumer.
        conn.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Value"] = 100 });

        var result = await conn.ReadValueAsync<double>("MAIN.Value", CancellationToken.None);

        Assert.Equal(100.0, result);
    }

    [Fact]
    public async Task NumericWidening_StoredInt_ReadAsLong_Succeeds()
    {
        using var conn = CreateSim();
        conn.SetInitialValues(new Dictionary<string, object?> { ["MAIN.X"] = 7 });

        var result = await conn.ReadValueAsync<long>("MAIN.X", CancellationToken.None);

        Assert.Equal(7L, result);
    }

    // =========================================================================
    // SimulatedAdsConnection — string-seeded value conversions (config-seeded sim)
    // =========================================================================

    [Fact]
    public async Task StringSeeded_ReadAsInt_Succeeds()
    {
        using var conn = CreateSim();
        conn.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Count"] = "42" });

        var result = await conn.ReadValueAsync<int>("MAIN.Count", CancellationToken.None);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task StringSeeded_ReadAsBool_Succeeds()
    {
        using var conn = CreateSim();
        conn.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Flag"] = "true" });

        var result = await conn.ReadValueAsync<bool>("MAIN.Flag", CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task StringSeeded_ReadAsBool_FalseString_Succeeds()
    {
        using var conn = CreateSim();
        conn.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Flag"] = "false" });

        var result = await conn.ReadValueAsync<bool>("MAIN.Flag", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task StringSeeded_InvariantCulture_DecimalReadAsDouble_Succeeds()
    {
        using var conn = CreateSim();
        // InvariantCulture: dot is the decimal separator, no locale ambiguity.
        conn.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Temp"] = "3.14" });

        var result = await conn.ReadValueAsync<double>("MAIN.Temp", CancellationToken.None);

        Assert.Equal(3.14, result, precision: 10);
    }

    [Fact]
    public async Task StringSeeded_ReadAsString_Identity_Succeeds()
    {
        using var conn = CreateSim();
        conn.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Label"] = "valve-open" });

        var result = await conn.ReadValueAsync<string>("MAIN.Label", CancellationToken.None);

        Assert.Equal("valve-open", result);
    }

    // =========================================================================
    // SimulatedAdsConnection — type mismatch → actionable InvalidCastException
    // =========================================================================

    [Fact]
    public async Task TypeMismatch_NonConvertibleType_ThrowsInvalidCastException_WithActionableMessage()
    {
        using var conn = CreateSim();
        // Store a Guid which is not IConvertible.
        var guid = Guid.NewGuid();
        conn.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Id"] = guid });

        var ex = await Assert.ThrowsAsync<InvalidCastException>(
            () => conn.ReadValueAsync<int>("MAIN.Id", CancellationToken.None));

        // Message must contain: symbol path, requested type, actual type.
        Assert.Contains("MAIN.Id", ex.Message);
        Assert.Contains("Int32", ex.Message);
        Assert.Contains("Guid", ex.Message);
    }

    [Fact]
    public async Task TypeMismatch_StringThatIsNotANumber_ThrowsInvalidCastException_WithSymbolInMessage()
    {
        using var conn = CreateSim();
        conn.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Count"] = "not-a-number" });

        var ex = await Assert.ThrowsAsync<InvalidCastException>(
            () => conn.ReadValueAsync<int>("MAIN.Count", CancellationToken.None));

        Assert.Contains("MAIN.Count", ex.Message);
        Assert.Contains("Int32", ex.Message);
    }

    // =========================================================================
    // SimulatedAdsConnection — null/missing symbol semantics
    // =========================================================================

    [Fact]
    public async Task MissingSymbol_ValueTypeT_ThrowsSymbolNotFound_WithSymbolAndTypeInMessage()
    {
        using var conn = CreateSim();
        // Symbol was never written — missing entirely. Same exception shape as a
        // real connection's unknown-symbol path (AdsErrorException/DeviceSymbolNotFound).

        var ex = await Assert.ThrowsAsync<AdsErrorException>(
            () => conn.ReadValueAsync<int>("MAIN.DoesNotExist", CancellationToken.None));

        Assert.Equal(AdsErrorCode.DeviceSymbolNotFound, ex.ErrorCode);
        Assert.Contains("MAIN.DoesNotExist", ex.Message);
        Assert.Contains("Int32", ex.Message);
    }

    [Fact]
    public async Task MissingSymbol_ReferenceTypeT_ThrowsSymbolNotFound_NotNull()
    {
        // Decision: missing symbol typed read ALWAYS throws (regardless of T being reference or value),
        // because there is no stored value to convert — this is semantically different from null stored.
        using var conn = CreateSim();

        var ex = await Assert.ThrowsAsync<AdsErrorException>(
            () => conn.ReadValueAsync<string>("MAIN.NeverWritten", CancellationToken.None));

        Assert.Equal(AdsErrorCode.DeviceSymbolNotFound, ex.ErrorCode);
        Assert.Contains("MAIN.NeverWritten", ex.Message);
    }

    [Fact]
    public async Task NullStoredValue_ValueTypeT_ThrowsInvalidCastException_WithActionableMessage()
    {
        using var conn = CreateSim();
        // Symbol was seeded with explicit null.
        conn.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Sensor"] = null });

        var ex = await Assert.ThrowsAsync<InvalidCastException>(
            () => conn.ReadValueAsync<int>("MAIN.Sensor", CancellationToken.None));

        Assert.Contains("MAIN.Sensor", ex.Message);
        Assert.Contains("Int32", ex.Message);
        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NullStoredValue_NullableValueTypeT_ReturnsNull()
    {
        using var conn = CreateSim();
        conn.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Sensor"] = null });

        var result = await conn.ReadValueAsync<int?>("MAIN.Sensor", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task NullStoredValue_ReferenceTypeT_ReturnsNull()
    {
        using var conn = CreateSim();
        conn.SetInitialValues(new Dictionary<string, object?> { ["MAIN.Name"] = null });

        var result = await conn.ReadValueAsync<string?>("MAIN.Name", CancellationToken.None);

        Assert.Null(result);
    }

    // =========================================================================
    // SimulatedAdsConnection — untyped object? overload still returns null for missing
    // =========================================================================

    [Fact]
    public async Task UntypedRead_MissingSymbol_StillReturnsNull_BackwardsCompat()
    {
        // The object? overload has always returned null for missing symbols;
        // typed reads diverge from this by throwing. Confirm the old behaviour is preserved.
        using var conn = CreateSim();

        var result = await conn.ReadValueAsync("MAIN.DoesNotExist", CancellationToken.None);

        Assert.Null(result);
    }

    // =========================================================================
    // SimulatedAdsConnection — typed write stores boxed value
    // =========================================================================

    [Fact]
    public async Task TypedWrite_StoresValue_ReadableByUntypedRead()
    {
        using var conn = CreateSim();
        await conn.WriteValueAsync<int>("MAIN.x", 99, CancellationToken.None);

        var untyped = await conn.ReadValueAsync("MAIN.x", CancellationToken.None);

        Assert.Equal(99, untyped);
    }

    // =========================================================================
    // SimulatedAdsConnection — cancellation
    // =========================================================================

    [Fact]
    public async Task TypedRead_CancelledToken_ThrowsOperationCanceledException()
    {
        using var conn = CreateSim();
        conn.SetInitialValues(new Dictionary<string, object?> { ["MAIN.x"] = 1 });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => conn.ReadValueAsync<int>("MAIN.x", cts.Token));
    }

    [Fact]
    public async Task TypedRead_CancelledToken_ExceptionCarriesCallerToken()
    {
        using var conn = CreateSim();
        conn.SetInitialValues(new Dictionary<string, object?> { ["MAIN.x"] = 1 });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(
            () => conn.ReadValueAsync<int>("MAIN.x", cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public async Task TypedWrite_CancelledToken_ThrowsOperationCanceledException()
    {
        using var conn = CreateSim();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => conn.WriteValueAsync<int>("MAIN.x", 42, cts.Token));
    }

    // =========================================================================
    // AdsConnectionFacade — typed calls route via SnapshotAsync
    // =========================================================================

    [Fact]
    public async Task Facade_TypedRead_RoutesToUnderlying_ReturnsTypedValue()
    {
        var facade = new AdsConnectionFacade(
            "plc1",
            new PlcTargetOptions { DisplayName = "PLC One" },
            new FakeTimeProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var recording = new TypedRecordingConnection("plc1") { IsConnected = true };
        recording.SetTypedReadResult<int>(77);
        facade.SetCurrent(recording);

        var result = await facade.ReadValueAsync<int>("MAIN.n", CancellationToken.None);

        Assert.Equal(77, result);
        Assert.Equal("MAIN.n", recording.LastTypedReadPath);
    }

    [Fact]
    public async Task Facade_TypedWrite_RoutesToUnderlying()
    {
        var facade = new AdsConnectionFacade(
            "plc1",
            new PlcTargetOptions { DisplayName = "PLC One" },
            new FakeTimeProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var recording = new TypedRecordingConnection("plc1") { IsConnected = true };
        facade.SetCurrent(recording);

        await facade.WriteValueAsync<int>("MAIN.y", 55, CancellationToken.None);

        Assert.Equal(("MAIN.y", (object)55), recording.LastTypedWrite);
    }

    [Fact]
    public async Task Facade_TypedRead_NoCurrentConnection_WaitsThenThrows()
    {
        // TimeoutMs is the wait bound. With no connection ever published, the typed
        // read waits the full TimeoutMs (of FAKE time) and then throws.
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1",
            new PlcTargetOptions { DisplayName = "PLC One", TimeoutMs = 500 },
            time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var readTask = facade.ReadValueAsync<int>("X", CancellationToken.None);
        Assert.False(readTask.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAsync<AdsConnectionUnavailableException>(() => readTask);
    }

    [Fact]
    public async Task Facade_TypedWrite_NoCurrentConnection_WaitsThenThrows()
    {
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1",
            new PlcTargetOptions { DisplayName = "PLC One", TimeoutMs = 500 },
            time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var writeTask = facade.WriteValueAsync<int>("X", 1, CancellationToken.None);
        Assert.False(writeTask.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAsync<AdsConnectionUnavailableException>(() => writeTask);
    }

    // =========================================================================
    // Test double — records typed calls for facade routing assertions
    // =========================================================================

    private sealed class TypedRecordingConnection : IManagedConnection
    {
        private object? _typedReadResult;

        public TypedRecordingConnection(string plcId) => PlcId = plcId;

        public string PlcId { get; }
        public string DisplayName => PlcId;
        public bool IsConnected { get; set; }

        public ConnectionState State => ConnectionState.Disconnected;

#pragma warning disable CS0067
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
#pragma warning restore CS0067

        public string? LastTypedReadPath { get; private set; }
        public (string Path, object? Value)? LastTypedWrite { get; private set; }

        public void SetTypedReadResult<T>(T value) => _typedReadResult = value;

        public Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct)
        {
            LastTypedReadPath = symbolPath;
            return Task.FromResult((T)_typedReadResult!);
        }

        public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct)
            => Task.FromResult(_typedReadResult);

        public Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct)
        {
            LastTypedWrite = (symbolPath, value);
            return Task.CompletedTask;
        }

        public Task WriteValueAsync(string symbolPath, object value, CancellationToken ct)
        {
            LastTypedWrite = (symbolPath, value);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, AdsValueResult>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, AdsValueResult>>(new Dictionary<string, AdsValueResult>());

        public Task<IReadOnlyDictionary<string, AdsValueResult>> WriteValuesAsync(IReadOnlyDictionary<string, object?> values, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, AdsValueResult>>(new Dictionary<string, AdsValueResult>());

        public Task<AdsState> GetAdsStateAsync(CancellationToken ct)
            => Task.FromResult(default(AdsState));

        public Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct)
            => Task.FromResult<IDisposable>(new DummyDisposable());

        public Task<IDisposable> SubscribeAsync<T>(string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Connect() => IsConnected = true;
        public void Disconnect() => IsConnected = false;
        public Task<bool> IsAliveAsync(CancellationToken ct) => Task.FromResult(true);
        public void ForceDisconnect() => IsConnected = false;
        public void LogSymbolTree(SymbolDumpOptions options) { }
        public void Dispose() => IsConnected = false;

        private sealed class DummyDisposable : IDisposable { public void Dispose() { } }
    }
}
