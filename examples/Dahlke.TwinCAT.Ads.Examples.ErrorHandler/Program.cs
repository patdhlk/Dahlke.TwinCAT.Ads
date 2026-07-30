using Dahlke.TwinCAT.Ads;
using Dahlke.TwinCAT.Ads.Alarms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Simulation by default so this runs with no TwinCAT installation; pass --real to
// talk to the PLC configured in appsettings.json.
var useRealPlc = args.Contains("--real");

// Pin the content root to the app directory so appsettings.json is found even when
// launched via `dotnet run --project` from the repo root.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

if (useRealPlc)
    builder.Services.AddTwinCatAds(builder.Configuration);
else
    builder.Services.AddTwinCatAdsSimulation(builder.Configuration);

// PlcAlarms:TextCatalog is "alarms.json" — a relative path, which AddTwinCatAdsAlarms
// resolves against the content root pinned above, so the catalog is found whether this
// runs from the repo root, from the output directory, or published.
builder.Services.AddTwinCatAdsAlarms(builder.Configuration);

// Registered AFTER AddTwinCatAdsAlarms deliberately: hosted services start in
// registration order, so the monitor's subscription is live before the driver's
// first write. Reversing these two lines loses the opening transitions.
if (!useRealPlc)
    builder.Services.AddHostedService<SimulatedPlcDriver>();

using var host = builder.Build();

var monitor = host.Services.GetRequiredService<IPlcAlarmMonitor>();

// Handlers run on the ADS notification thread and hold up this target's next
// snapshot, so they must be quick — print and return, never await work here.
monitor.AlarmChanged += (_, transition) =>
{
    var alarm = transition.Alarm;
    Console.WriteLine(
        $"[{transition.Kind.ToString().ToUpperInvariant()}] {alarm.Key} " +
        $"({alarm.Severity}) — {alarm.Text ?? "(no catalog text)"}");
};

await host.RunAsync();

/// <summary>
/// Drives the SIMULATED PLC through an alarm lifecycle so the example shows something
/// without hardware, then stops the host. Not registered in <c>--real</c> mode, where
/// the PLC supplies the transitions and the example runs until Ctrl+C.
/// </summary>
/// <remarks>
/// <para>
/// The simulated connection is a flat store of dotted paths, so the alarm array is
/// written code-first as an <c>object?[]</c>. Each write passes a NEW array instance —
/// change detection falls back to reference equality, so a mutated array would not
/// fire the subscription.
/// </para>
/// <para>
/// The lifecycle is scripted rather than random so the console output is the same on
/// every run: raised, joined by a second alarm on the same machine, cleared while still
/// unacknowledged, then acknowledged — which is what finally ends it.
/// </para>
/// </remarks>
internal sealed class SimulatedPlcDriver(
    IAdsConnectionPool pool,
    IPlcAlarmMonitor monitor,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    private const string PlcId = "plc1";
    private const string AlarmArrayPath = "GVL.Errors";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = pool.GetConnection(PlcId);

        // An empty array first, so the monitor has a shape to bind before anything
        // interesting happens.
        await WriteArrayAsync(connection, stoppingToken);
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

        // Raise a jam.
        await WriteArrayAsync(connection, stoppingToken, Jam(isActive: true));
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        // A second alarm on the SAME equipment — distinct because sKey differs. Keying
        // on Id (the BMK) instead would collapse these two into one.
        await WriteArrayAsync(connection, stoppingToken, Jam(isActive: true), Overtemperature());
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        // The jam clears, but nobody has acknowledged it — it stays outstanding.
        await WriteArrayAsync(connection, stoppingToken, Jam(isActive: false), Overtemperature());
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        Report("after the fault cleared");

        // Acknowledge it through the monitor: that writes IsAcked on the PLC entry,
        // after verifying the slot still holds this alarm.
        var acknowledged = await monitor.AcknowledgeAsync(PlcId, "BMK1Err404", stoppingToken);
        Console.WriteLine($"AcknowledgeAsync(\"BMK1Err404\") -> {acknowledged}");

        // A real PLC would push the changed array itself; the simulated store holds only
        // the paths that were written and derives nothing from a member write, so the
        // array reading that reflects the acknowledgement is echoed here.
        await WriteArrayAsync(
            connection, stoppingToken, Jam(isActive: false, isAcked: true), Overtemperature());
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

        Report("after the acknowledgement");

        lifetime.StopApplication();
    }

    private void Report(string when)
    {
        Console.WriteLine($"Outstanding {when}:");

        foreach (var alarm in monitor.GetOutstanding())
        {
            Console.WriteLine(
                $"  {alarm.Key} on {alarm.EquipmentId} ({alarm.Severity}) " +
                $"active={alarm.IsActive} acknowledged={alarm.IsAcknowledged}");
        }
    }

    /// <summary>
    /// Puts <paramref name="entries"/> on the simulated PLC as the whole alarm array.
    /// </summary>
    /// <remarks>
    /// A real PLC exposes the array AND every member path under it: writing
    /// <c>GVL.Errors</c> makes <c>GVL.Errors[0].sKey</c> readable in the same breath. The
    /// simulated store is FLAT — it holds exactly the paths written — so each entry's
    /// scalar members are mirrored onto their own paths to model what a PLC would expose.
    /// Without that, <see cref="IPlcAlarmMonitor.AcknowledgeAsync"/> could not read the
    /// slot's occupant back before writing, and would fail with
    /// <c>DeviceSymbolNotFound</c>.
    /// </remarks>
    private static async Task WriteArrayAsync(
        IAdsConnection connection, CancellationToken ct, params object?[] entries)
    {
        for (var slot = 0; slot < entries.Length; slot++)
        {
            foreach (var (member, value) in (Dictionary<string, object?>)entries[slot]!)
            {
                // Nested containers (PLCTimeStamp) are skipped: nothing addresses them by
                // member path.
                if (value is not IDictionary<string, object?>)
                    await connection.WriteValueAsync($"{AlarmArrayPath}[{slot}].{member}", value!, ct);
            }
        }

        // A NEW array instance every time — see the type-level remarks.
        await connection.WriteValueAsync(AlarmArrayPath, entries, ct);
    }

    private static Dictionary<string, object?> Jam(bool isActive, bool isAcked = false) =>
        Entry("BMK1Err404", 404, severity: 3, isActive, isAcked);

    private static Dictionary<string, object?> Overtemperature() =>
        Entry("BMK1Err500", 500, severity: 2, isActive: true, isAcked: false);

    private static Dictionary<string, object?> Entry(
        string sKey, uint errorCode, int severity, bool isActive, bool isAcked) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sKey"] = sKey,
            ["Id"] = "BMK1",
            ["ErrorCode"] = errorCode,
            ["ErrorType"] = severity,
            ["IsActive"] = isActive,
            ["NeedsAck"] = true,
            ["IsAcked"] = isAcked,
            // Held constant per alarm: a timestamp that advanced while the fault stayed
            // active would (correctly) report Reoccurred on every snapshot.
            ["PLCTimeStamp"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["wYear"] = (ushort)2026, ["wMonth"] = (ushort)7, ["wDayOfWeek"] = (ushort)4,
                ["wDay"] = (ushort)30, ["wHour"] = (ushort)9, ["wMinute"] = (ushort)15,
                ["wSecond"] = (ushort)0, ["wMilliseconds"] = (ushort)0,
            },
        };
}
