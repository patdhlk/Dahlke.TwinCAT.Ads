using Dahlke.TwinCAT.Ads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Pass --real to connect to an actual PLC (requires the targets in
// appsettings.json to be reachable). Without it, the in-memory
// simulation is used and the example runs anywhere.
var useRealPlc = args.Contains("--real");

// Pin the content root to the app directory so appsettings.json is found
// even when launched via `dotnet run --project` from the repo root.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

if (useRealPlc)
    builder.Services.AddTwinCatAds(builder.Configuration);
else
    builder.Services.AddTwinCatAdsSimulation(builder.Configuration);

using var host = builder.Build();

// Start the hosted services so the pool establishes its connections.
await host.StartAsync();

var pool = host.Services.GetRequiredService<IAdsConnectionPool>();
var ct = CancellationToken.None;

Console.WriteLine($"Mode: {(useRealPlc ? "real PLC" : "simulation")}");
Console.WriteLine();

// List all configured connections.
foreach (var (plcId, conn) in pool.GetAllConnections())
    Console.WriteLine($"  {plcId} ({conn.DisplayName}) connected: {conn.IsConnected}");

// GetConnection throws UnknownPlcTargetException for an unconfigured id;
// for a configured target it always returns the stable facade (never null).
// Check IsConnected to detect an outage before performing operations.
var connection = pool.GetConnection("plc1");
if (!connection.IsConnected)
{
    Console.WriteLine("plc1 is not connected — check appsettings.json and PLC reachability.");
    await host.StopAsync();
    return;
}

// Write a single value, then read it back.
await connection.WriteValueAsync("GVL.Counter", (short)42, ct);
var counter = await connection.ReadValueAsync("GVL.Counter", ct);
Console.WriteLine($"GVL.Counter = {counter}");

// Batch write and batch read.
await connection.WriteValuesAsync(new Dictionary<string, object?>
{
    ["GVL.SetpointTemperature"] = 21.5f,
    ["GVL.PumpRunning"] = true,
}, ct);

var values = await connection.ReadValuesAsync(
    ["GVL.SetpointTemperature", "GVL.PumpRunning"], ct);
// Each entry carries a per-symbol AdsValueResult: Succeeded plus Value (or Error).
foreach (var (symbol, result) in values)
    Console.WriteLine($"{symbol} = {(result.Succeeded ? result.Value : $"ERROR: {result.Error?.Message}")}");

// Query the PLC run state.
var state = await connection.GetAdsStateAsync(ct);
Console.WriteLine($"ADS state: {state}");

// Subscribe to a symbol for a few seconds (with a real PLC the callback
// fires on value changes; the simulation accepts the subscription as a no-op).
using var subscription = await connection.SubscribeAsync(
    "GVL.Counter", cycleTimeMs: 200,
    (symbol, value) => Console.WriteLine($"Notification: {symbol} = {value}"), ct);

await Task.Delay(TimeSpan.FromSeconds(3), ct);

await host.StopAsync();
