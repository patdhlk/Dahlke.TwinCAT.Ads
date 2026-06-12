using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// TDD tests for C13: non-nullable <see cref="IAdsConnectionPool.GetConnection"/>
/// that throws <see cref="UnknownPlcTargetException"/> for unconfigured identifiers,
/// <see cref="IAdsConnectionPool.TryGetConnection"/> as the non-throwing variant,
/// and the constructor-level eager facade creation that makes GetConnection total
/// from construction (before StartAsync).
/// </summary>
public class C13_UnknownPlcTargetAndTryGetConnectionTests
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(15);

    private static (AdsConnectionPool pool, FakeConnectionFactory factory, FakeTimeProvider time, AdsRouterReadySignal signal)
        CreatePool(params string[] plcIds)
    {
        if (plcIds.Length == 0) plcIds = ["plc1"];

        var targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in plcIds)
            targets[id] = new PlcTargetOptions { DisplayName = id, AmsNetId = "1.2.3.4.5.6" };

        var adsOptions = new TwinCatAdsOptions { Targets = targets };
        var factory = new FakeConnectionFactory();
        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal();

        var pool = new AdsConnectionPool(
            Options.Create(adsOptions),
            factory,
            signal,
            NullLoggerFactory.Instance,
            time);

        return (pool, factory, time, signal);
    }

    // =========================================================================
    // UnknownPlcTargetException message and property contract
    // =========================================================================

    [Fact]
    public void UnknownPlcTargetException_MessageContainsBadIdAndConfiguredIds()
    {
        var ex = new UnknownPlcTargetException("plc01", ["plc1", "plc2"]);

        Assert.Equal("plc01", ex.PlcId);
        Assert.Contains("plc01", ex.Message);
        Assert.Contains("plc1", ex.Message);
        Assert.Contains("plc2", ex.Message);
    }

    [Fact]
    public void UnknownPlcTargetException_ZeroConfiguredTargets_MessageSaysNoTargets()
    {
        var ex = new UnknownPlcTargetException("plc01", []);

        Assert.Equal("plc01", ex.PlcId);
        Assert.Contains("plc01", ex.Message);
        Assert.Contains("No targets are configured", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownPlcTargetException_InnerExceptionOverload_SetsAllProperties()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new UnknownPlcTargetException("plc01", ["plc1"], inner);

        Assert.Equal("plc01", ex.PlcId);
        Assert.Same(inner, ex.InnerException);
        Assert.Contains("plc01", ex.Message);
        Assert.Contains("plc1", ex.Message);
    }

    [Fact]
    public void UnknownPlcTargetException_ExplicitMessageOverload_SetsAllProperties()
    {
        var ex = new UnknownPlcTargetException("plc01", "custom message", null);

        Assert.Equal("plc01", ex.PlcId);
        Assert.Equal("custom message", ex.Message);
    }

    [Fact]
    public void UnknownPlcTargetException_ConfiguredIdsAreSortedInMessage()
    {
        var ex = new UnknownPlcTargetException("bad", ["zebra", "alpha", "mango"]);

        // All ids must appear; message must contain them in alphabetical order
        var alphaIdx = ex.Message.IndexOf("alpha", StringComparison.OrdinalIgnoreCase);
        var mangoIdx = ex.Message.IndexOf("mango", StringComparison.OrdinalIgnoreCase);
        var zebraIdx = ex.Message.IndexOf("zebra", StringComparison.OrdinalIgnoreCase);

        Assert.True(alphaIdx < mangoIdx, "alpha should appear before mango");
        Assert.True(mangoIdx < zebraIdx, "mango should appear before zebra");
    }

    // =========================================================================
    // Facade creation is eager (constructor, not StartAsync)
    // =========================================================================

    [Fact]
    public void GetConnection_KnownId_PreStart_ReturnsFacade_NonNull()
    {
        // Facades are created in the constructor — GetConnection is total from
        // construction, even before StartAsync is called.
        var (pool, _, _, _) = CreatePool("plc1");

        var facade = pool.GetConnection("plc1");

        Assert.NotNull(facade);
        Assert.IsType<AdsConnectionFacade>(facade);
        Assert.False(facade.IsConnected); // not connected yet — loop hasn't started
    }

    [Fact]
    public void GetConnection_KnownId_CaseInsensitive_PreStart_ReturnsFacade()
    {
        var (pool, _, _, _) = CreatePool("plc1");

        // Upper-case variant must resolve to the same target.
        var facade = pool.GetConnection("PLC1");

        Assert.NotNull(facade);
        Assert.IsType<AdsConnectionFacade>(facade);
    }

    [Fact]
    public void GetConnection_MultipleCasings_ReturnSameFacadeInstance()
    {
        var (pool, _, _, _) = CreatePool("plc1");

        var a = pool.GetConnection("plc1");
        var b = pool.GetConnection("PLC1");
        var c = pool.GetConnection("Plc1");

        Assert.Same(a, b);
        Assert.Same(b, c);
    }

    // =========================================================================
    // GetConnection — non-nullable, throws for unknown id
    // =========================================================================

    [Fact]
    public void GetConnection_UnknownId_ThrowsUnknownPlcTargetException()
    {
        var (pool, _, _, _) = CreatePool("plc1", "plc2");

        var ex = Assert.Throws<UnknownPlcTargetException>(() => pool.GetConnection("plc99"));

        Assert.Equal("plc99", ex.PlcId);
    }

    [Fact]
    public void GetConnection_UnknownId_ExceptionMessageContainsBadId()
    {
        var (pool, _, _, _) = CreatePool("plc1", "plc2");

        var ex = Assert.Throws<UnknownPlcTargetException>(() => pool.GetConnection("typo-plc"));

        Assert.Contains("typo-plc", ex.Message);
    }

    [Fact]
    public void GetConnection_UnknownId_ExceptionMessageContainsConfiguredIds()
    {
        var (pool, _, _, _) = CreatePool("plc1", "plc2");

        var ex = Assert.Throws<UnknownPlcTargetException>(() => pool.GetConnection("plc99"));

        Assert.Contains("plc1", ex.Message);
        Assert.Contains("plc2", ex.Message);
    }

    [Fact]
    public async Task GetConnection_UnknownId_PostStart_StillThrowsUnknownPlcTarget()
    {
        var (pool, factory, _, signal) = CreatePool("plc1");
        factory.Enqueue(new FakeManagedConnection("plc1"));

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        var ex = Assert.Throws<UnknownPlcTargetException>(() => pool.GetConnection("never-configured"));
        Assert.Equal("never-configured", ex.PlcId);
        Assert.Contains("plc1", ex.Message);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    // =========================================================================
    // TryGetConnection — non-throwing variant
    // =========================================================================

    [Fact]
    public void TryGetConnection_KnownId_ReturnsTrueAndFacade()
    {
        var (pool, _, _, _) = CreatePool("plc1");

        var found = pool.TryGetConnection("plc1", out var connection);

        Assert.True(found);
        Assert.NotNull(connection);
        Assert.IsType<AdsConnectionFacade>(connection);
    }

    [Fact]
    public void TryGetConnection_UnknownId_ReturnsFalseAndNull()
    {
        var (pool, _, _, _) = CreatePool("plc1");

        var found = pool.TryGetConnection("plc99", out var connection);

        Assert.False(found);
        Assert.Null(connection);
    }

    [Fact]
    public void TryGetConnection_CaseInsensitive_ResolvesKnownId()
    {
        var (pool, _, _, _) = CreatePool("plc1");

        var found = pool.TryGetConnection("PLC1", out var connection);

        Assert.True(found);
        Assert.NotNull(connection);
    }

    [Fact]
    public void TryGetConnection_DoesNotThrow_ForUnknownId()
    {
        var (pool, _, _, _) = CreatePool("plc1");

        // Must not throw — that is the entire contract of TryGetConnection.
        var ex = Record.Exception(() => pool.TryGetConnection("does-not-exist", out _));
        Assert.Null(ex);
    }

    // =========================================================================
    // Facade stability across StartAsync (ctor creation means same instance)
    // =========================================================================

    [Fact]
    public async Task GetConnection_SameFacadeInstanceBeforeAndAfterStart()
    {
        var (pool, factory, _, signal) = CreatePool("plc1");
        factory.Enqueue(new FakeManagedConnection("plc1"));

        var facadeBeforeStart = pool.GetConnection("plc1");

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        var facadeAfterStart = pool.GetConnection("plc1");

        Assert.Same(facadeBeforeStart, facadeAfterStart);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }

    [Fact]
    public async Task GetConnection_KnownId_AfterStart_ReturnsFacade_IsConnectedFalseUntilLoop()
    {
        var (pool, factory, _, signal) = CreatePool("plc1");

        // Connect throws persistently — facade exists but never becomes connected.
        factory.Enqueue(new FakeManagedConnection("plc1") { ConnectShouldThrow = true });

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        var facade = pool.GetConnection("plc1");
        Assert.NotNull(facade);
        Assert.IsType<AdsConnectionFacade>(facade);
        // Connection loop hasn't succeeded — IsConnected remains false.
        Assert.False(facade.IsConnected);

        await pool.StopAsync(CancellationToken.None).WaitAsync(RealTimeout);
    }
}
