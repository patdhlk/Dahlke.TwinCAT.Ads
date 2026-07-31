using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Alarms.Tests;

/// <summary>Unit tests for the shipped default dialect.</summary>
public class ErrorHandlerAlarmDialectTests
{
    private const string ArrayPath = "MAIN.ErrorHandler.aHmiAlarms";

    /// <summary>The numbering the reference rack currently publishes.</summary>
    private static readonly AdsEnumMember[] RackNumbering =
    [
        new("ERROR", 0), new("ABORTED", 1), new("NOT_READY", 2), new("NOT_FOUND", 3),
        new("INVALID_DATA", 4), new("SUCCESS", 5), new("BUSY", 6),
    ];

    /// <summary>A deliberately different numbering, same names.</summary>
    private static readonly AdsEnumMember[] ShuffledNumbering =
    [
        new("SUCCESS", 0), new("ERROR", 1), new("ABORTED", 2), new("NOT_READY", 3),
        new("NOT_FOUND", 4), new("INVALID_DATA", 5), new("BUSY", 6),
    ];

    /// <summary>
    /// Explicit, non-contiguous values in an order deliberately unrelated to them.
    /// </summary>
    /// <remarks>
    /// Both numberings above run <c>0..6</c> in declaration order, so for them
    /// <c>members[value]</c> and "the member with that value" happen to agree — an implementation
    /// indexing by POSITION passes every test that uses only those two. Explicit values
    /// (<c>SUCCESS := 100</c>) are ordinary ST, and <c>GetEnumMembersAsync</c> promises
    /// declaration order, never dense zero-based values.
    /// </remarks>
    private static readonly AdsEnumMember[] NonContiguousNumbering =
    [
        new("NOT_READY", 3), new("SUCCESS", 100), new("BUSY", 12), new("ERROR", 7),
        new("NOT_FOUND", 42), new("ABORTED", 1), new("INVALID_DATA", 55),
    ];

    private static PlcAlarm Alarm(string key = "Test_Err_60") => new()
    {
        Key = key, EquipmentId = "Test", ErrorCode = 60, Severity = AlarmSeverity.Error,
        IsActive = false, NeedsAcknowledgement = true, IsAcknowledged = false,
        PlcTimestamp = new DateTime(2026, 7, 31, 7, 7, 46), SlotIndex = 0, PlcId = "plc1",
    };

    private static PlcAlarmTargetOptions Options() =>
        new() { SymbolPath = ArrayPath, CycleTimeMs = 200 };

    private static async Task<bool> AcknowledgeAsync(
        IReadOnlyList<AdsEnumMember> numbering, string resultName)
    {
        var value = numbering.Single(m => m.Name == resultName).Value;
        var conn = new FakeRpcConnection(numbering, (short)value);
        var dialect = new ErrorHandlerAlarmDialect();

        return await dialect.AcknowledgeAsync(
            new AlarmAcknowledgeContext(Alarm(), conn, "plc1", Options()), CancellationToken.None);
    }

    [Fact]
    public async Task Success_ReturnsTrue()
    {
        Assert.True(await AcknowledgeAsync(RackNumbering, "SUCCESS"));
    }

    [Fact]
    public async Task NotFound_ReturnsFalse()
    {
        Assert.False(await AcknowledgeAsync(RackNumbering, "NOT_FOUND"));
    }

    [Theory]
    [InlineData("ERROR")]
    [InlineData("ABORTED")]
    [InlineData("INVALID_DATA")]
    [InlineData("NOT_READY")]
    [InlineData("BUSY")]
    public async Task OtherOutcomes_Throw_CarryingTheName(string name)
    {
        var ex = await Assert.ThrowsAsync<PlcAlarmAcknowledgeException>(
            () => AcknowledgeAsync(RackNumbering, name));

        Assert.Equal(name, ex.ReturnCodeName);
    }

    [Fact]
    public async Task TheSameNames_UnderADifferentNumbering_BehaveIdentically()
    {
        // This is the only test that distinguishes name resolution from a hardcoded table:
        // a numeric implementation passes every other test in this class and fails this one.
        // It is not here to support any particular historical numbering — it exercises the
        // resolution mechanism. The reference rack and its own source disagree today, which
        // is exactly the condition this guards.
        Assert.True(await AcknowledgeAsync(ShuffledNumbering, "SUCCESS"));
        Assert.False(await AcknowledgeAsync(ShuffledNumbering, "NOT_FOUND"));
        await Assert.ThrowsAsync<PlcAlarmAcknowledgeException>(
            () => AcknowledgeAsync(ShuffledNumbering, "BUSY"));
    }

    [Fact]
    public async Task NonContiguousValues_ResolveByName_NotByPosition()
    {
        // The second discriminating test, and the only one covering "no assumption that member
        // order implies meaning". Both other fixtures are 0..6 in declaration order, so
        // `members[(int)raw].Name` passes all of them; here SUCCESS := 100 sits at index 1 and
        // that implementation reads past the end of a seven-member list.
        Assert.True(await AcknowledgeAsync(NonContiguousNumbering, "SUCCESS"));
        Assert.False(await AcknowledgeAsync(NonContiguousNumbering, "NOT_FOUND"));
        await Assert.ThrowsAsync<PlcAlarmAcknowledgeException>(
            () => AcknowledgeAsync(NonContiguousNumbering, "BUSY"));
    }

    [Fact]
    public async Task ValueMatchingNoPublishedMember_Throws()
    {
        var conn = new FakeRpcConnection(RackNumbering, (short)99);
        var dialect = new ErrorHandlerAlarmDialect();

        var ex = await Assert.ThrowsAsync<PlcAlarmAcknowledgeException>(
            () => dialect.AcknowledgeAsync(
                new AlarmAcknowledgeContext(Alarm(), conn, "plc1", Options()), CancellationToken.None));

        Assert.Null(ex.ReturnCodeName);
        Assert.Equal(99, ex.ReturnCode);
    }

    [Fact]
    public async Task Acknowledge_CallsTheMethodOnTheDerivedInstancePath_WithTheAlarmKey()
    {
        var conn = new FakeRpcConnection(RackNumbering, (short)5);
        var dialect = new ErrorHandlerAlarmDialect();

        await dialect.AcknowledgeAsync(
            new AlarmAcknowledgeContext(Alarm("Test1_Err_60"), conn, "plc1", Options()),
            CancellationToken.None);

        Assert.Equal("MAIN.ErrorHandler", conn.LastPath);
        Assert.Equal("AcknowledgeAlarm", conn.LastMethod);
        Assert.Equal(["Test1_Err_60"], conn.LastParameters);
    }

    [Fact]
    public async Task ExplicitInstancePath_OverridesTheDerivedOne()
    {
        var conn = new FakeRpcConnection(RackNumbering, (short)5);
        var options = Options();
        options.AcknowledgeInstancePath = "GVL.Handler";
        var dialect = new ErrorHandlerAlarmDialect();

        await dialect.AcknowledgeAsync(
            new AlarmAcknowledgeContext(Alarm(), conn, "plc1", options), CancellationToken.None);

        Assert.Equal("GVL.Handler", conn.LastPath);
    }

    [Fact]
    public async Task TheResultTypeIsResolvedByItsPlcName()
    {
        var conn = new FakeRpcConnection(RackNumbering, (short)5);
        var dialect = new ErrorHandlerAlarmDialect();

        await dialect.AcknowledgeAsync(
            new AlarmAcknowledgeContext(Alarm(), conn, "plc1", Options()), CancellationToken.None);

        // Hardware-verified spelling. Without this the name is asserted nowhere and a typo ships
        // green, failing only against a real PLC.
        Assert.Equal("deaReturnType", conn.LastEnumTypeName);
    }

    [Fact]
    public async Task TheResultTypeIsResolvedBeforeTheRpc_SoItsFailureCannotStrandAnAcknowledgedAlarm()
    {
        var conn = new FakeRpcConnection(RackNumbering, (short)5)
        {
            // What the core throws when deaReturnType resolves to something that is not an enum.
            EnumMembersFailure = () => new InvalidOperationException("not an enumeration"),
        };
        var dialect = new ErrorHandlerAlarmDialect();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dialect.AcknowledgeAsync(
                new AlarmAcknowledgeContext(Alarm(), conn, "plc1", Options()), CancellationToken.None));

        // The whole point: resolved after the RPC, the alarm would already be acknowledged and the
        // operator would be told to retry something that had worked.
        Assert.Null(conn.LastPath);
    }

    [Theory]
    [InlineData(true)]       // BOOL: IConvertible would fold it to 1 — a real member under most numberings
    [InlineData("SUCCESS")]  // STRING: IConvertible accepts it, then escapes as a raw FormatException
    [InlineData(1.5)]
    public async Task ANonIntegralReturn_Throws_NamingWhatItGot(object returnValue)
    {
        var conn = new FakeRpcConnection(RackNumbering, returnValue);
        var dialect = new ErrorHandlerAlarmDialect();

        var ex = await Assert.ThrowsAsync<PlcAlarmAcknowledgeException>(
            () => dialect.AcknowledgeAsync(
                new AlarmAcknowledgeContext(Alarm(), conn, "plc1", Options()), CancellationToken.None));

        Assert.Contains(returnValue.GetType().Name, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASymbolPathWithNoOwningBlock_IsAConfigurationError_ThatNeverReachesThePlc()
    {
        var conn = new FakeRpcConnection(RackNumbering, (short)5);
        var options = new PlcAlarmTargetOptions { SymbolPath = "Errors", CycleTimeMs = 200 };
        var dialect = new ErrorHandlerAlarmDialect();

        // ThrowsAsync matches the type EXACTLY, so this also pins that it is not the derived
        // PlcAlarmAcknowledgeException — which would have to fabricate a ReturnCode for a call
        // that was never made, and a fabricated 0 is SUCCESS under some numberings.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dialect.AcknowledgeAsync(
                new AlarmAcknowledgeContext(Alarm(), conn, "plc1", options), CancellationToken.None));

        Assert.Contains("AcknowledgeInstancePath", ex.Message, StringComparison.Ordinal);
        Assert.Null(conn.LastPath);
    }
}

/// <summary>
/// The two connection members the dialect is allowed to touch, and nothing else.
/// </summary>
/// <remarks>
/// Every other member throws rather than returning a plausible value: a dialect that
/// starts reading or writing symbols to acknowledge an alarm is a behaviour change these
/// tests should catch, not one they should quietly accommodate.
/// </remarks>
internal sealed class FakeRpcConnection(IReadOnlyList<AdsEnumMember> members, object? returnValue)
    : IAdsConnection
{
    /// <summary>The instance path of the last RPC call, or <see langword="null"/> if none.</summary>
    public string? LastPath { get; private set; }

    /// <summary>The method name of the last RPC call.</summary>
    public string? LastMethod { get; private set; }

    /// <summary>The parameters the last RPC call carried.</summary>
    public object?[] LastParameters { get; private set; } = [];

    /// <summary>The type name the last enum resolution asked for.</summary>
    public string? LastEnumTypeName { get; private set; }

    /// <summary>
    /// When set, <c>GetEnumMembersAsync</c> throws this instead of answering — the ordinary
    /// failures the real one has (type not published, type not an enum, timeout, cancellation).
    /// </summary>
    public Func<Exception>? EnumMembersFailure { get; set; }

    public Task<AdsRpcResult> InvokeRpcMethodAsync(
        string symbolPath, string methodName, object?[] parameters, CancellationToken ct)
    {
        LastPath = symbolPath;
        LastMethod = methodName;
        LastParameters = parameters;

        return Task.FromResult(new AdsRpcResult(returnValue, []));
    }

    public Task<IReadOnlyList<AdsEnumMember>> GetEnumMembersAsync(
        string typeName, CancellationToken ct)
    {
        LastEnumTypeName = typeName;

        if (EnumMembersFailure is { } failure)
            throw failure();

        return Task.FromResult(members);
    }

    // Nothing below is reachable from the dialect. Explicit event accessors rather than a
    // field-like event so an unused-event warning is never introduced here.
    public string PlcId => throw new NotSupportedException();
    public string DisplayName => throw new NotSupportedException();
    public bool IsConnected => throw new NotSupportedException();
    public ConnectionState State => throw new NotSupportedException();

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged
    {
        add => throw new NotSupportedException();
        remove => throw new NotSupportedException();
    }

    public Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<AdsValueResult> ReadValueWithMetadataAsync(string symbolPath, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task WriteValueAsync(string symbolPath, object value, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, AdsValueResult>> ReadValuesAsync(
        IEnumerable<string> symbolPaths, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, AdsValueResult>> WriteValuesAsync(
        IReadOnlyDictionary<string, object?> values, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<AdsState> GetAdsStateAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<AdsDeviceInfo> GetDeviceInfoAsync(CancellationToken ct) =>
        throw new NotSupportedException();
    public Task WriteControlAsync(AdsState state, ushort deviceState, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<IDisposable> SubscribeAsync(
        string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<IDisposable> SubscribeAsync<T>(
        string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<IDisposable> SubscribeAsync(
        string symbolPath, int cycleTimeMs, Action<AdsNotification> callback, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(string? parentPath, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(
        string? parentPath, bool includeChildren, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<AdsSymbolInfo>> SearchSymbolsAsync(
        string pattern, bool includeChildren, CancellationToken ct) =>
        throw new NotSupportedException();
}
