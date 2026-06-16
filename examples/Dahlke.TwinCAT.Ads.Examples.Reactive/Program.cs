using System.Reactive.Linq;
using Dahlke.TwinCAT.Ads;
using Dahlke.TwinCAT.Ads.Reactive;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Runs entirely against the in-memory simulation — no PLC or router required.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddTwinCatAdsSimulation(builder.Configuration);

using var host = builder.Build();

// Resolve the pool BEFORE starting the host so we can subscribe to connection
// state and observe the initial Disconnected -> Connecting -> Connected
// transitions the simulated loops raise during StartAsync.
var pool = host.Services.GetRequiredService<IAdsConnectionPool>();

using var stateSub = pool.ObserveAllConnectionStates()
    .Subscribe(e => Console.WriteLine($"[state] {e.PlcId}: {e.PreviousState} -> {e.State}"));

await host.StartAsync();

var conn = pool.GetConnection("plc1");

// Typed value stream with Rx composition: only emit hot setpoints, de-duplicated,
// at most one per 250 ms.
using var tempSub = conn.ObserveValue<float>("GVL.Temp", cycleTimeMs: 100)
    .Select(change => change.Value)
    .Where(t => t > 50f)
    .DistinctUntilChanged()
    .Throttle(TimeSpan.FromMilliseconds(250))
    .Subscribe(t => Console.WriteLine($"[hot]   GVL.Temp = {t} °C"));

// Untyped value stream.
using var counterSub = conn.ObserveValue("GVL.Counter", cycleTimeMs: 100)
    .Subscribe(change => Console.WriteLine($"[count] {change.Symbol} = {change.Value}"));

// Drive changing values into the simulated target to feed the streams.
var ct = CancellationToken.None;
float[] temps = [20f, 55f, 55f, 60f, 30f, 75f];
for (var i = 0; i < temps.Length; i++)
{
    await conn.WriteValueAsync<float>("GVL.Temp", temps[i], ct);
    await conn.WriteValueAsync("GVL.Counter", (short)i, ct);
    await Task.Delay(300, ct);
}

// Let the final throttled value flush.
await Task.Delay(500, ct);

await host.StopAsync();
