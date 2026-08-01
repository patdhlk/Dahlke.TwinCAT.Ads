using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Every async member of the consumer-facing contracts carries
/// <c>CancellationToken ct = default</c>, so a caller with no token to pass writes nothing
/// rather than <c>CancellationToken.None</c>.
///
/// These tests are unusual in that COMPILING them is most of the assertion: each call below
/// omits the token entirely, which is a compile error unless the parameter is optional. The
/// runtime assertions then pin the second half — that an omitted token behaves exactly like an
/// explicitly-passed <see cref="CancellationToken.None"/>, rather than a pre-cancelled or
/// otherwise surprising default.
///
/// Coverage:
/// - <see cref="IAdsConnection"/> through the interface (defaults live on the interface, so a
///   call through it is the case consumers actually hit).
/// - <see cref="SimulatedAdsConnection"/> through the concrete type — a documented testing
///   pattern, and a separate declaration site that has to carry the defaults independently.
/// - Overload resolution is unchanged: omitting the token must not silently re-bind a call to a
///   different overload of the same name.
/// </summary>
public class DefaultCancellationTokenTests
{
    private static SimulatedAdsConnection CreateSim()
        => new("test-plc", "Test PLC", NullLoggerFactory.Instance);

    // =========================================================================
    // Through the interface — the consumer-facing case
    // =========================================================================

    [Fact]
    public async Task ReadAndWrite_ThroughInterface_CompileAndRunWithNoToken()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;

        await conn.WriteValueAsync("MAIN.Counter", 42);
        var typed = await conn.ReadValueAsync<int>("MAIN.Counter");
        var untyped = await conn.ReadValueAsync("MAIN.Counter");
        var withMetadata = await conn.ReadValueWithMetadataAsync("MAIN.Counter");

        Assert.Equal(42, typed);
        Assert.Equal(42, untyped);
        Assert.Equal(42, withMetadata.Value);
    }

    [Fact]
    public async Task Batches_ThroughInterface_CompileAndRunWithNoToken()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;

        var written = await conn.WriteValuesAsync(new Dictionary<string, object?>
        {
            ["MAIN.A"] = 1,
            ["MAIN.B"] = true,
        });
        var read = await conn.ReadValuesAsync(["MAIN.A", "MAIN.B"]);

        Assert.All(written.Values, r => Assert.True(r.Succeeded));
        Assert.Equal(1, read["MAIN.A"].GetValue<int>());
        Assert.True(read["MAIN.B"].GetValue<bool>());
    }

    [Fact]
    public async Task DeviceAndBrowseMembers_ThroughInterface_CompileAndRunWithNoToken()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;
        await conn.WriteValueAsync("MAIN.Speed", 1500);

        var state = await conn.GetAdsStateAsync();
        var info = await conn.GetDeviceInfoAsync();
        var roots = await conn.GetSymbolsAsync(null, includeChildren: false);
        var matches = await conn.SearchSymbolsAsync("Speed", includeChildren: false);

        Assert.Equal(AdsState.Run, state);
        Assert.NotNull(info.Name);
        Assert.NotEmpty(roots);
        Assert.Contains(matches, s => s.InstancePath == "MAIN.Speed");
    }

    [Fact]
    public async Task Subscribe_ThroughInterface_CompilesAndRunsWithNoToken()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;
        var seen = new List<object?>();

        using var sub = await conn.SubscribeAsync("MAIN.Temp", 100, (_, v) => seen.Add(v));
        await conn.WriteValueAsync("MAIN.Temp", 21.5);

        Assert.Equal([21.5], seen);
    }

    // =========================================================================
    // Through the concrete simulated type — a separate declaration site
    // =========================================================================

    [Fact]
    public async Task ConcreteSimulatedConnection_CompilesAndRunsWithNoToken()
    {
        using var conn = CreateSim();

        await conn.WriteValueAsync("MAIN.Flag", true);
        var value = await conn.ReadValueAsync<bool>("MAIN.Flag");

        Assert.True(value);
    }

    // =========================================================================
    // An omitted token behaves as CancellationToken.None, not as something else
    // =========================================================================

    [Fact]
    public async Task OmittedToken_MatchesExplicitNone()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;
        await conn.WriteValueAsync("MAIN.Counter", 7, CancellationToken.None);

        var omitted = await conn.ReadValueAsync<int>("MAIN.Counter");
        var explicitNone = await conn.ReadValueAsync<int>("MAIN.Counter", CancellationToken.None);

        Assert.Equal(explicitNone, omitted);
    }

    [Fact]
    public async Task OmittedToken_DoesNotSuppressCancellation_WhenOneIsPassed()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => conn.ReadValueAsync<int>("MAIN.Counter", cts.Token));
    }

    // =========================================================================
    // Overload resolution is unchanged by the defaults
    // =========================================================================

    [Fact]
    public async Task OmittingToken_BindsTypedReadToTypedOverload()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;
        await conn.WriteValueAsync("MAIN.Speed", 1500);

        // The typed overload converts; the untyped one boxes. Distinguishing the two by their
        // RESULT is what proves the omitted token did not re-bind the call.
        double widened = await conn.ReadValueAsync<double>("MAIN.Speed");
        object? boxed = await conn.ReadValueAsync("MAIN.Speed");

        Assert.Equal(1500d, widened);
        Assert.Equal(1500, Assert.IsType<int>(boxed));
    }

    [Fact]
    public async Task OmittingToken_KeepsGetSymbolsAsyncOverloadsDistinct()
    {
        using var sim = CreateSim();
        IAdsConnection conn = sim;
        await conn.WriteValueAsync("MAIN.Nested.Leaf", 1);

        // Two arguments still selects the (parentPath, includeChildren) overload, not the
        // (parentPath, ct) one — the bool has no default, so it cannot be omitted.
        var flat = await conn.GetSymbolsAsync(null, includeChildren: false);

        Assert.All(flat, s => Assert.Null(s.Children));
    }

    // =========================================================================
    // The raw-channel contract carries the same defaults
    // =========================================================================

    [Fact]
    public async Task RawChannel_CompilesAndRunsWithNoToken()
    {
        var transport = new InMemoryManagedRawConnection();
        IAdsRawChannel channel = new AdsRawChannel(
            "1.2.3.4.5.6", 0xFFFF,
            (_, _) => transport,
            new AdsRawChannelOptions(),
            NullLogger.Instance,
            new FakeTimeProvider());
        transport.Seed(0x11, 1001, [1, 2, 3, 4]);

        var buffer = new byte[4];
        var read = await channel.ReadAsync(0x11, 1001, buffer);
        await channel.WriteAsync(0x11, 1001, new byte[] { 5, 6, 7, 8 });

        Assert.Equal(4, read);
        Assert.Equal([1, 2, 3, 4], buffer);
    }
}
