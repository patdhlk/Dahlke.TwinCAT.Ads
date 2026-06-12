using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Tests that the <see cref="AdsConnectionPool"/> correctly gates the
/// <c>LogSymbolTree</c> call on <see cref="SymbolDumpOptions.Enabled"/>
/// and forwards the configured <see cref="SymbolDumpOptions"/> instance.
/// </summary>
public class AdsConnectionPoolSymbolDumpTests
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(15);

    private static (AdsConnectionPool pool, FakeManagedConnection conn, AdsRouterReadySignal signal)
        CreatePoolWithDump(SymbolDumpOptions symbolDump)
    {
        var targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["plc1"] = new PlcTargetOptions { DisplayName = "plc1", AmsNetId = "1.2.3.4.5.6" },
        };

        var adsOptions = new TwinCatAdsOptions
        {
            Targets = targets,
            Diagnostics = new AdsDiagnosticsOptions { SymbolDump = symbolDump },
        };

        var conn = new FakeManagedConnection("plc1");
        var factory = new FakeConnectionFactory();
        factory.Enqueue(conn);

        var time = new FakeTimeProvider();
        var signal = new AdsRouterReadySignal();

        var pool = new AdsConnectionPool(
            Options.Create(adsOptions),
            factory,
            signal,
            NullLogger<AdsConnectionPool>.Instance,
            time);

        return (pool, conn, signal);
    }

    // Since the C11 facade redesign, GetConnection returns the stable facade, not
    // the underlying managed connection. Wait on the facade's current routing
    // target instead of on GetConnection identity.
    private static async Task WaitForConnection(AdsConnectionPool pool, string plcId, object expected)
    {
        var deadline = DateTime.UtcNow + RealTimeout;
        while (!ReferenceEquals(CurrentOf(pool, plcId), expected))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Facade for '{plcId}' never routed to the expected managed instance.");
            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }
    }

    private static IManagedConnection? CurrentOf(AdsConnectionPool pool, string plcId)
        => ((AdsConnectionFacade)pool.GetConnection(plcId)!).CurrentForTesting;

    /// <summary>
    /// When <see cref="SymbolDumpOptions.Enabled"/> is <see langword="true"/>,
    /// the pool calls <c>LogSymbolTree</c> on the connection immediately after
    /// a successful connect, passing the configured options.
    /// </summary>
    [Fact]
    public async Task SymbolDump_Enabled_CallsLogSymbolTree_WithConfiguredOptions()
    {
        var dumpOpts = new SymbolDumpOptions
        {
            Enabled = true,
            MaxDepth = 3,
            Prefixes = ["GVL_Test"],
        };

        var (pool, conn, signal) = CreatePoolWithDump(dumpOpts);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await conn.ConnectCalled.WaitAsync(RealTimeout);
        await WaitForConnection(pool, "plc1", conn);

        // Give the loop a moment to execute the LogSymbolTree call that
        // follows the _connections[plcId] = ads assignment.
        var deadline = DateTime.UtcNow + RealTimeout;
        while (Volatile.Read(ref conn.LogSymbolTreeCount) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(5));

        Assert.Equal(1, conn.LogSymbolTreeCount);
        Assert.NotNull(conn.LastLogSymbolTreeOptions);
        Assert.True(conn.LastLogSymbolTreeOptions!.Enabled);
        Assert.Equal(3, conn.LastLogSymbolTreeOptions.MaxDepth);
        Assert.Equal(["GVL_Test"], conn.LastLogSymbolTreeOptions.Prefixes);

        await pool.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// When <see cref="SymbolDumpOptions.Enabled"/> is <see langword="false"/> (default),
    /// <c>LogSymbolTree</c> is never called.
    /// </summary>
    [Fact]
    public async Task SymbolDump_Disabled_DoesNotCallLogSymbolTree()
    {
        var dumpOpts = new SymbolDumpOptions { Enabled = false };

        var (pool, conn, signal) = CreatePoolWithDump(dumpOpts);

        signal.SetReady();
        await pool.StartAsync(CancellationToken.None);

        await conn.ConnectCalled.WaitAsync(RealTimeout);
        await WaitForConnection(pool, "plc1", conn);

        // Give the loop a small window to mistakenly call LogSymbolTree.
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.Equal(0, conn.LogSymbolTreeCount);
        Assert.Null(conn.LastLogSymbolTreeOptions);

        await pool.StopAsync(CancellationToken.None);
    }
}
