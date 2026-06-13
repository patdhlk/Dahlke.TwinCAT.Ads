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

// -----------------------------------------------------------------
// Typed write then typed read (preferred pattern)
// -----------------------------------------------------------------
await connection.WriteValueAsync<float>("GVL.SetpointTemperature", 21.5f, ct);
float temp = await connection.ReadValueAsync<float>("GVL.SetpointTemperature", ct);
Console.WriteLine($"GVL.SetpointTemperature = {temp} °C");

// Write and read back an integer symbol.
await connection.WriteValueAsync("GVL.Counter", (short)42, ct);
short counter = await connection.ReadValueAsync<short>("GVL.Counter", ct);
Console.WriteLine($"GVL.Counter = {counter}");

// -----------------------------------------------------------------
// Batch write and batch read (single ADS sum command per direction)
// -----------------------------------------------------------------
await connection.WriteValuesAsync(new Dictionary<string, object?>
{
    ["GVL.SetpointTemperature"] = 23.0f,
    ["GVL.PumpRunning"] = true,
}, ct);

var results = await connection.ReadValuesAsync(
    ["GVL.SetpointTemperature", "GVL.PumpRunning"], ct);

// Each entry carries a per-symbol AdsValueResult: Succeeded plus Value (or Error).
foreach (var (symbol, result) in results)
    Console.WriteLine($"{symbol} = {(result.Succeeded ? result.Value : $"ERROR: {result.Error?.Message}")}");

// Typed access on a batch result:
if (results["GVL.SetpointTemperature"].Succeeded)
{
    float setpoint = results["GVL.SetpointTemperature"].GetValue<float>();
    Console.WriteLine($"Setpoint (typed): {setpoint} °C");
}

// -----------------------------------------------------------------
// ADS state
// -----------------------------------------------------------------
var state = await connection.GetAdsStateAsync(ct);
Console.WriteLine($"ADS state: {state}");

// -----------------------------------------------------------------
// Typed subscription — durable across reconnects
// -----------------------------------------------------------------
// The callback fires on a background thread; it must be thread-safe.
// With a real PLC it fires on value change; the simulation fires when
// a new (different) value is written.
using var typedSub = await connection.SubscribeAsync<float>(
    "GVL.SetpointTemperature",
    cycleTimeMs: 200,
    (symbol, value) => Console.WriteLine($"Typed notification: {symbol} = {value} °C"),
    CancellationToken.None);

// Write a changed value to trigger the simulated subscription callback.
await connection.WriteValueAsync<float>("GVL.SetpointTemperature", 25.0f, ct);
await Task.Delay(TimeSpan.FromMilliseconds(100), ct);

// -----------------------------------------------------------------
// Untyped subscription
// -----------------------------------------------------------------
using var untypedSub = await connection.SubscribeAsync(
    "GVL.Counter",
    cycleTimeMs: 200,
    (symbol, value) => Console.WriteLine($"Untyped notification: {symbol} = {value}"),
    CancellationToken.None);

await connection.WriteValueAsync("GVL.Counter", (short)99, ct);
await Task.Delay(TimeSpan.FromSeconds(1), ct);

await host.StopAsync();
