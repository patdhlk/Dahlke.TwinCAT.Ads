using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Alarms.Tests;

/// <summary>
/// End-to-end tests for <see cref="PlcAlarmMonitor"/> over a SIMULATED connection —
/// DI, options binding, subscription, binder and store together.
/// </summary>
/// <remarks>
/// The simulated connection is a flat store of dotted paths, so the alarm array is
/// seeded code-first as an <c>object?[]</c> of dictionaries. Change detection falls
/// back to reference equality, so writing a fresh array instance fires the
/// subscription.
/// </remarks>
public class PlcAlarmMonitorTests
{
    private const string PlcId = "plc1";
    private const string Path = "GVL.Errors";

    private static Dictionary<string, object?> Entry(
        string sKey, bool isActive = true, bool isAcked = false, uint errorCode = 404) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sKey"] = sKey,
            ["Id"] = "BMK1",
            ["ErrorCode"] = errorCode,
            ["ErrorType"] = 3,
            ["IsActive"] = isActive,
            ["NeedsAck"] = true,
            ["IsAcked"] = isAcked,
            ["PLCTimeStamp"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["wYear"] = (ushort)2026, ["wMonth"] = (ushort)6, ["wDayOfWeek"] = (ushort)3,
                ["wDay"] = (ushort)17, ["wHour"] = (ushort)12, ["wMinute"] = (ushort)0,
                ["wSecond"] = (ushort)0, ["wMilliseconds"] = (ushort)0,
            },
        };

    /// <summary>
    /// Builds a host with one simulated target whose alarm array is seeded empty,
    /// starts it, and returns the running host plus its monitor.
    /// </summary>
    private static async Task<(IHost Host, IPlcAlarmMonitor Monitor)> StartAsync()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddTwinCatAdsSimulation(o =>
        {
            o.Targets[PlcId] = new PlcTargetOptions
            {
                AmsNetId = "1.2.3.4.5.6",
                InitialValues = { [Path] = Array.Empty<object?>() },
            };
        });

        builder.Services.Configure<PlcAlarmsOptions>(o =>
            o.Targets[PlcId] = new PlcAlarmTargetOptions { SymbolPath = Path, CycleTimeMs = 50 });

        builder.Services.AddTwinCatAdsAlarms(builder.Configuration);

        var host = builder.Build();
        await host.StartAsync();

        return (host, host.Services.GetRequiredService<IPlcAlarmMonitor>());
    }

    /// <summary>
    /// Puts <paramref name="entries"/> on the simulated PLC as the whole alarm array.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A real PLC exposes the array AND every member path under it: writing
    /// <c>GVL.Errors</c> makes <c>GVL.Errors[0].sKey</c> readable in the same breath. The
    /// simulated connection's store is FLAT — it holds exactly the paths that were
    /// written and derives nothing from a written container — so this helper mirrors each
    /// entry's scalar members onto their own paths to model what a PLC would expose.
    /// Without it, the monitor's acknowledgement (which verifies a slot's occupant by
    /// reading <c>...[i].sKey</c>) would need a container-walking fallback that no real
    /// target could ever exercise.
    /// </para>
    /// <para>
    /// Members are written BEFORE the array so the notification the array write fires
    /// already sees member paths that agree with it.
    /// </para>
    /// </remarks>
    private static async Task WriteArrayAsync(IHost host, params object?[] entries)
    {
        var connection = host.Services.GetRequiredService<IAdsConnectionPool>().GetConnection(PlcId);

        for (var slot = 0; slot < entries.Length; slot++)
        {
            foreach (var (member, value) in (Dictionary<string, object?>)entries[slot]!)
            {
                // Nested containers (PLCTimeStamp) are skipped: nothing addresses them by
                // member path, and mirroring them would need recursion for no coverage.
                if (value is not IDictionary<string, object?>)
                    await connection.WriteValueAsync($"{Path}[{slot}].{member}", value!, CancellationToken.None);
            }
        }

        // A NEW array instance every time: the simulated store's change comparer falls
        // back to reference equality for object?[], so a mutated array would not fire.
        await connection.WriteValueAsync(Path, entries, CancellationToken.None);
    }

    /// <summary>Waits for a condition the notification thread will satisfy.</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);

        Assert.True(condition(), "Condition was not satisfied within the timeout.");
    }

    [Fact]
    public async Task RaisedAlarm_AppearsInOutstandingAndRaisesTheEvent()
    {
        var (host, monitor) = await StartAsync();
        using var _ = host;

        var transitions = new List<AlarmTransition>();
        monitor.AlarmChanged += (_, t) => { lock (transitions) { transitions.Add(t); } };

        await WriteArrayAsync(host, Entry("BMK1Err404"));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 1);

        lock (transitions)
        {
            Assert.Contains(transitions, t => t.Kind == AlarmTransitionKind.Raised);
        }

        await host.StopAsync();
    }

    [Fact]
    public async Task ClearedAndAcknowledgedAlarm_LeavesOutstanding()
    {
        var (host, monitor) = await StartAsync();
        using var _ = host;

        await WriteArrayAsync(host, Entry("BMK1Err404"));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 1);

        await WriteArrayAsync(host, Entry("BMK1Err404", isActive: false, isAcked: true));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 0);

        await host.StopAsync();
    }

    [Fact]
    public async Task GetOutstanding_FiltersByPlcId()
    {
        var (host, monitor) = await StartAsync();
        using var _ = host;

        await WriteArrayAsync(host, Entry("BMK1Err404"));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 1);

        Assert.Single(monitor.GetOutstanding(PlcId));
        Assert.Empty(monitor.GetOutstanding("someOtherPlc"));

        await host.StopAsync();
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotStopDelivery()
    {
        var (host, monitor) = await StartAsync();
        using var _ = host;

        var delivered = 0;
        monitor.AlarmChanged += (_, _) => throw new InvalidOperationException("boom");
        monitor.AlarmChanged += (_, _) => Interlocked.Increment(ref delivered);

        await WriteArrayAsync(host, Entry("BMK1Err404"));
        await WaitForAsync(() => Volatile.Read(ref delivered) > 0);

        await host.StopAsync();
    }

    [Fact]
    public async Task AcknowledgeAsync_WritesIsAckedOnTheEntry()
    {
        var (host, monitor) = await StartAsync();
        using var _ = host;

        await WriteArrayAsync(host, Entry("BMK1Err404"));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 1);

        var acknowledged = await monitor.AcknowledgeAsync(PlcId, "BMK1Err404", CancellationToken.None);

        Assert.True(acknowledged);

        var pool = host.Services.GetRequiredService<IAdsConnectionPool>();
        var written = await pool.GetConnection(PlcId)
            .ReadValueAsync<bool>($"{Path}[0].IsAcked", CancellationToken.None);
        Assert.True(written);

        await host.StopAsync();
    }

    [Fact]
    public async Task AcknowledgeAsync_UnknownAlarm_ReturnsFalse()
    {
        var (host, monitor) = await StartAsync();
        using var _ = host;

        var acknowledged = await monitor.AcknowledgeAsync(PlcId, "NoSuchAlarm", CancellationToken.None);

        Assert.False(acknowledged);

        await host.StopAsync();
    }

    [Fact]
    public async Task AcknowledgeAsync_UnknownTarget_ReturnsFalse()
    {
        var (host, monitor) = await StartAsync();
        using var _ = host;

        var acknowledged = await monitor.AcknowledgeAsync(
            "someOtherPlc", "BMK1Err404", CancellationToken.None);

        Assert.False(acknowledged);

        await host.StopAsync();
    }

    [Fact]
    public async Task AcknowledgeAsync_SlotNoLongerHoldsTheAlarm_ReturnsFalse()
    {
        // Slots are reused. Acknowledging by slot index without verifying the
        // occupant would acknowledge whatever alarm has since landed there.
        var (host, monitor) = await StartAsync();
        using var _ = host;

        await WriteArrayAsync(host, Entry("BMK1Err404"));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 1);

        var pool = host.Services.GetRequiredService<IAdsConnectionPool>();
        await pool.GetConnection(PlcId)
            .WriteValueAsync($"{Path}[0].sKey", "BMK9Err999", CancellationToken.None);

        var acknowledged = await monitor.AcknowledgeAsync(PlcId, "BMK1Err404", CancellationToken.None);

        Assert.False(acknowledged);

        await host.StopAsync();
    }
}
