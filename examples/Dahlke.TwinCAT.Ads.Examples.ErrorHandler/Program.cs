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
/// Acknowledgement is a PLC method call, not a write, so this driver also plays the part
/// of the function block: it seeds <c>deaReturnType</c>'s members and an
/// <c>AcknowledgeAlarm</c> handler on the simulated connection. The handler records the
/// acknowledged key, and every array written afterwards derives each entry's
/// <c>IsAcked</c> from that record — so <c>[ACKNOWLEDGED]</c> below is caused by the
/// acknowledgement actually reaching the dialect, not by a literal in the script.
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

    // The array is a member of the function block that owns acknowledgement, so the shipped
    // dialect derives 'MAIN.ErrorHandler' by trimming the last segment. AcknowledgeInstancePath
    // is left unset in appsettings.json precisely to exercise that derivation — a layout where
    // the array lives elsewhere would have to set it. This is the reference rack's layout, and
    // the one spelling used throughout this repository; --real against any other PLC needs
    // SymbolPath changed to match that PLC.
    private const string AlarmArrayPath = "MAIN.ErrorHandler.aHmiAlarms";
    private const string HandlerInstancePath = "MAIN.ErrorHandler";

    // What the seeded AcknowledgeAlarm method remembers. A real FB_ErrorHandler holds this
    // state itself; here it is the only thing connecting the RPC to the arrays written after
    // it. Case-insensitive, matching how the monitor looks an alarm up by key.
    private readonly HashSet<string> _acknowledgedKeys = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = pool.GetConnection(PlcId);

        SeedAcknowledgeMethod();

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

        // Acknowledge it through the monitor: that calls AcknowledgeAlarm on the function
        // block over ADS, passing the alarm's key, and reads the returned deaReturnType by
        // name. Here the call lands on the handler seeded above.
        var acknowledged = await monitor.AcknowledgeAsync(PlcId, "BMK1Err404", stoppingToken);
        Console.WriteLine($"AcknowledgeAsync(\"BMK1Err404\") -> {acknowledged}");

        // A real PLC would push the changed array itself; the simulated store holds only the
        // paths that were written and derives nothing from a method call, so the array reading
        // that reflects the acknowledgement is echoed here. Nothing below says "acknowledged" —
        // Jam reads that off what the seeded method recorded.
        await WriteArrayAsync(
            connection, stoppingToken, Jam(isActive: false), Overtemperature());
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

        Report("after the acknowledgement");

        lifetime.StopApplication();
    }

    /// <summary>
    /// Teaches the simulated PLC the one method and the one enum the shipped dialect needs.
    /// </summary>
    /// <remarks>
    /// Both seeding calls are mandatory, and deliberately so: an unseeded RPC or an unseeded
    /// enum THROWS on the simulated connection rather than answering something plausible. An
    /// acknowledgement that quietly does nothing looks exactly like one that worked, which is
    /// the failure this whole path was rebuilt to make impossible — so simulation refuses to
    /// stage it.
    /// </remarks>
    private void SeedAcknowledgeMethod()
    {
        if (!pool.TryGetSimulatedConnection(PlcId, out var sim))
            throw new InvalidOperationException(
                $"'{PlcId}' is not a simulated target; this driver only runs in simulation mode.");

        // The numbering the reference rack publishes. It is seeded here only so the handler
        // below has something to return — the dialect matches on the NAME 'SUCCESS', so a PLC
        // that numbers these differently still reads correctly.
        sim.SetEnumMembers("deaReturnType",
        [
            new AdsEnumMember("SUCCESS", 0), new AdsEnumMember("ERROR", 1),
            new AdsEnumMember("ABORTED", 2), new AdsEnumMember("NOT_READY", 3),
            new AdsEnumMember("NOT_FOUND", 4), new AdsEnumMember("INVALID_DATA", 5),
            new AdsEnumMember("BUSY", 6),
        ]);

        sim.SetRpcHandler(HandlerInstancePath, "AcknowledgeAlarm", args =>
        {
            // Mirror what the real function block does: remember the acknowledgement so the
            // next array this driver writes carries IsAcked = true for that key. Returns 0,
            // which is SUCCESS under the numbering seeded above.
            _acknowledgedKeys.Add((string)args[0]!);
            return new AdsRpcResult((short)0, []);
        });
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
    /// <c>MAIN.ErrorHandler.aHmiAlarms</c> makes <c>MAIN.ErrorHandler.aHmiAlarms[0].sKey</c>
    /// readable in the same breath. The simulated store is FLAT — it holds exactly the paths
    /// written — so each entry's scalar members are mirrored onto their own paths to model
    /// what a PLC would expose, and so anything browsing this target's symbol tree sees the
    /// shape it would on hardware. Acknowledgement no longer depends on it: it names the
    /// alarm by key and never addresses a slot.
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

    private Dictionary<string, object?> Jam(bool isActive) =>
        Entry("BMK1Err404", 404, severity: 3, isActive);

    private Dictionary<string, object?> Overtemperature() =>
        Entry("BMK1Err500", 500, severity: 2, isActive: true);

    /// <remarks>
    /// <c>IsAcked</c> is NOT a parameter. It is read off what the seeded
    /// <c>AcknowledgeAlarm</c> handler recorded, so no line of this script can claim an
    /// alarm was acknowledged unless an acknowledgement really went through the monitor and
    /// the dialect.
    /// </remarks>
    private Dictionary<string, object?> Entry(
        string sKey, uint errorCode, int severity, bool isActive) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sKey"] = sKey,
            ["Id"] = "BMK1",
            ["ErrorCode"] = errorCode,
            ["ErrorType"] = severity,
            ["IsActive"] = isActive,
            ["NeedsAck"] = true,
            ["IsAcked"] = _acknowledgedKeys.Contains(sKey),
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
