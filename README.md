# Dahlke.TwinCAT.Ads

[![CI](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/actions/workflows/ci.yml/badge.svg)](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Dahlke.TwinCAT.Ads.svg)](https://www.nuget.org/packages/Dahlke.TwinCAT.Ads)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

A .NET library for TwinCAT ADS with durable connections, typed symbol access, simulation mode, and ASP.NET Core integration.

## Features

- **Typed reads and writes** — `ReadValueAsync<T>` / `WriteValueAsync<T>` with automatic widening conversions and invariant-culture string parsing; `object?` overloads as a dynamic escape hatch
- **Stable connection facades** — `GetConnection` returns one object per target whose identity never changes; reconnects are invisible; cached references never go stale
- **Wait-then-throw semantics** — operations wait up to the configured `TimeoutMs` for a connection, then throw `AdsConnectionUnavailableException`; `TimeoutException` for hardware/network stalls; `OperationCanceledException` only for caller cancellation
- **Durable subscriptions** — survive reconnects automatically; the returned `IDisposable` stays valid through outages; simulated subscriptions fire on changed writes
- **Reactive (Rx) companion** — optional `Dahlke.TwinCAT.Ads.Reactive` package surfaces value-change and connection-state notifications as `IObservable<T>` streams
- **PLC alarm tracking** — the optional `Dahlke.TwinCAT.Ads.Alarms` package keeps a live set of outstanding alarms from a TwinCAT alarm array and streams `Raised` / `Acknowledged` / `Cleared` / `Reoccurred` / `Ended` transitions; acknowledgement calls the PLC's own acknowledging method over ADS, with a health check and a JSON text catalog
- **PLC method calls and enum metadata** — `InvokeRpcMethodAsync` calls a method on a function-block instance (in and out parameters, and the return value); `GetEnumMembersAsync` reads a PLC enumeration's members from the running program, so a returned code can be read by name instead of by number
- **ADS sum commands** — batch writes, and batch reads of scalars, execute as a single round-trip on real connections; per-symbol `AdsValueResult` for granular success/failure
- **Connection state observability** — `State` property (tri-state), `IsConnected` snapshot, `ConnectionStateChanged` event
- **Raw ADS channels** — `IAdsRawChannelFactory` addresses any `(amsNetId, port)` by index group and index offset for targets the symbol API cannot reach (EtherCAT, the TwinCAT system service); cached, never disposed by the caller, with durable device notifications and a seedable simulation store
- **Per-target simulation** — `ConnectionMode.Real | Simulated` per target; mixed fleets supported; `InitialValues` seeding; `AddTwinCatAdsSimulation` forces all targets — and raw channels — to simulated
- **Health check** — Healthy / Degraded / Unhealthy with per-target data via `AddTwinCatAdsHealthCheck()`
- **Options validation at startup** — malformed AMS Net IDs, invalid ports, non-positive timeouts, and malformed raw-channel seed entries fail boot with actionable messages
- **Embedded ADS router with retry** — retries with backoff (2 s → 30 s cap); pool startup never blocks on the router

## Installation

```bash
dotnet add package Dahlke.TwinCAT.Ads
```

Optionally add the Rx companion for `IObservable<T>` streams (see [Reactive (Rx) companion](#reactive-rx-companion)). It depends on the core package, so this pulls both:

```bash
dotnet add package Dahlke.TwinCAT.Ads.Reactive
```

Optionally add the alarms companion to track a PLC alarm array (see [PLC alarms](#plc-alarms)). It also depends on the core package:

```bash
dotnet add package Dahlke.TwinCAT.Ads.Alarms
```

## Quick Start

### Configuration-first (recommended for server applications)

**`appsettings.json`:**

```json
{
  "AmsRouter": {
    "NetId": "127.0.0.1.1.1"
  },
  "PlcTargets": {
    "plc1": {
      "AmsNetId": "192.168.1.10.1.1",
      "Port": 851,
      "DisplayName": "Main PLC",
      "TimeoutMs": 5000
    }
  }
}
```

**`Program.cs`:**

```csharp
// Real PLC connections
builder.Services.AddTwinCatAds(builder.Configuration);

// Or: force all targets to simulation mode (no TwinCAT required)
builder.Services.AddTwinCatAdsSimulation(builder.Configuration);
```

### Code-first (no IConfiguration required)

```csharp
builder.Services.AddTwinCatAds(o =>
{
    o.Targets["plc1"] = new PlcTargetOptions
    {
        AmsNetId = "192.168.1.10.1.1",
        Port = 851,
        DisplayName = "Main PLC",
        TimeoutMs = 5000,
    };
});

// Simulation mode, code-first:
builder.Services.AddTwinCatAdsSimulation(o =>
{
    o.Targets["plc1"] = new PlcTargetOptions
    {
        DisplayName = "Simulated PLC",
        InitialValues = { ["GVL.Temp"] = 21.5f },
    };
});
```

### Combo (config binding + code-first override)

```csharp
// Config binding runs first; the lambda layers on top.
builder.Services.AddTwinCatAds(builder.Configuration, o =>
{
    o.Diagnostics.SymbolDump.Prefixes.Add("GVL");
});
```

## Reading and Writing Values

### Typed reads (preferred)

```csharp
public class TempService(IAdsConnectionPool pool)
{
    public async Task<float> GetTemperatureAsync(CancellationToken ct)
    {
        var conn = pool.GetConnection("plc1");
        return await conn.ReadValueAsync<float>("GVL.Temp", ct);
    }
}
```

Supported conversions: widening numeric casts (e.g. PLC `INT` stored as `int` readable as `double`), and string-seeded simulation values via `Convert.ChangeType` with `CultureInfo.InvariantCulture` (e.g. `"42"` → `int`, `"true"` → `bool`).

### Dynamic (untyped) reads

```csharp
object? value = await conn.ReadValueAsync("GVL.Counter", ct);
```

Use the untyped overload when the target type is not known at compile time (generic dashboards, reflection-driven serialisation).

### Typed writes

```csharp
await conn.WriteValueAsync<float>("GVL.Setpoint", 23.5f, ct);
// Or let the compiler infer T:
await conn.WriteValueAsync("GVL.Counter", (short)42, ct);
```

## Batch Operations

```csharp
// Batch write: IReadOnlyDictionary<string, object?> input
await conn.WriteValuesAsync(new Dictionary<string, object?>
{
    ["GVL.Setpoint"] = 21.5f,
    ["GVL.PumpRunning"] = true,
}, ct);

// Batch read: IReadOnlyDictionary<string, AdsValueResult> result
var results = await conn.ReadValuesAsync(["GVL.Setpoint", "GVL.PumpRunning"], ct);

foreach (var (symbol, result) in results)
{
    if (result.Succeeded)
        Console.WriteLine($"{symbol} = {result.Value}");
    else
        Console.WriteLine($"{symbol} FAILED: {result.Error!.Message}");
}

// Typed access on a result:
float setpoint = results["GVL.Setpoint"].GetValue<float>();
```

On real connections a batch write is a single ADS sum command. A batch **read** partitions by category: scalars, strings and enums share one sum command, while structs, function blocks, unions and arrays are decoded individually so their members come back as a tree rather than an opaque value. So an all-scalar batch costs one round-trip, and a batch containing containers costs one plus the container decodes. Every result carries `TypeName` and `Category` either way.

A per-symbol failure is captured in `AdsValueResult.Error` and does not abort the batch. A whole-batch timeout throws `TimeoutException`; caller cancellation throws `OperationCanceledException`.

## Symbol Browsing and Metadata

```csharp
// Browse one level — pass includeChildren: false for interactive drill-down.
// The two-argument overload defaults to true, which projects the ENTIRE subtree.
var roots = await conn.GetSymbolsAsync(null, includeChildren: false, ct);
foreach (var s in roots)
    Console.WriteLine($"{s.InstancePath} : {s.TypeName} ({s.Category}, {s.ByteSize}B)");

// Search across the whole tree by substring
var motors = await conn.SearchSymbolsAsync("Motor", includeChildren: false, ct);

// Read a value together with its PLC type
var result = await conn.ReadValueWithMetadataAsync("MAIN.Motor", ct);
Console.WriteLine($"{result.TypeName} = {result.Value}");   // ST_Motor = Dictionary<string, object?>
```

Browsing is bounded by `PlcTargetOptions.SymbolBrowseTimeoutMs` (default 30 s) rather than `TimeoutMs`, since uploading the symbol table can take longer than a typical read or write. That timeout bounds how long the *caller* waits — the underlying upload is a blocking Beckhoff call and continues in the background if abandoned.

`ReadValueWithMetadataAsync` decodes structs, function blocks and unions to `Dictionary<string, object?>` keyed by member name, arrays to `object?[]`, and passes scalars through unchanged. `ALIAS`, `PROGRAM`, `POINTER` and `REFERENCE` categories are currently passed through as-is, so a value of one of those types may surface as a raw TwinCAT object rather than a neutral tree.

## Subscriptions

### Typed subscription (preferred)

```csharp
using var sub = await conn.SubscribeAsync<float>(
    "GVL.Temp",
    cycleTimeMs: 200,
    (symbol, value) => Console.WriteLine($"{symbol} = {value}"),
    CancellationToken.None);

// Subscription survives reconnects; dispose to remove permanently.
```

### Untyped subscription

```csharp
using var sub = await conn.SubscribeAsync(
    "GVL.Counter",
    cycleTimeMs: 500,
    (symbol, value) => Console.WriteLine($"{symbol} = {value}"),
    CancellationToken.None);
```

Subscriptions are durable: owned by the stable facade, not the underlying connection. When a reconnect occurs the subscription is automatically re-registered against the new connection. Callbacks fire on a background thread — they must be thread-safe and must not block. A `null` notification value with a value-type `T` is dropped (Warning logged). Dispose is idempotent and thread-safe.

### Notification metadata

```csharp
using var sub = await conn.SubscribeAsync("GVL.Temp", cycleTimeMs: 200,
    n => Console.WriteLine($"[{n.Timestamp:O}] {n.SymbolPath} ({n.TypeName}) = {n.Value}"),
    CancellationToken.None);
```

Carries the same durability guarantees as the untyped overload, plus the symbol's PLC type name and the PLC-reported timestamp of the change. Struct, function block and array notifications are decoded off the notification thread and so may be delivered slightly later — and, under a fast burst, out of order relative to scalar notifications.

## Reactive (Rx) companion

The optional **`Dahlke.TwinCAT.Ads.Reactive`** package exposes subscriptions and connection state as `IObservable<T>` streams (built on `System.Reactive`). Install it alongside the core package only if you want Rx — the core package never depends on `System.Reactive`.

```csharp
using Dahlke.TwinCAT.Ads.Reactive;
using System.Reactive.Linq;

var conn = pool.GetConnection("plc1");

// Typed value stream — cold: each Subscribe opens its own ADS notification,
// disposing it deletes the notification. Durable across reconnects.
using var sub = conn.ObserveValue<float>("GVL.Temp", cycleTimeMs: 200)
    .Select(change => change.Value)
    .Where(t => t > 50f)
    .DistinctUntilChanged()
    .Subscribe(t => Console.WriteLine($"Hot: {t} °C"));

// Connection state across every configured target (each event carries its PlcId).
using var states = pool.ObserveAllConnectionStates()
    .Subscribe(e => Console.WriteLine($"{e.PlcId}: {e.PreviousState} -> {e.State}"));
```

Each notification is an `AdsValueChange<T>` record (`Symbol`, `Value`). Notifications arrive on a background thread — add `.ObserveOn(...)` before updating UI. To share one underlying ADS notification among multiple subscribers, add `.Publish().RefCount()`. See [`examples/Dahlke.TwinCAT.Ads.Examples.Reactive`](examples/Dahlke.TwinCAT.Ads.Examples.Reactive/) for a runnable demo.

## PLC alarms

The optional **`Dahlke.TwinCAT.Ads.Alarms`** package tracks a TwinCAT alarm array — an `ARRAY[..] OF ST_ErrorEntry` whose entries carry `sKey`, `Id`, `ErrorCode`, `ErrorType`, `IsActive`, `NeedsAck`, `IsAcked` and a `PLCTimeStamp`.

```bash
dotnet add package Dahlke.TwinCAT.Ads.Alarms
```

```json
{
  "PlcAlarms": {
    "TextCatalog": "alarms.json",
    "Targets": {
      "plc1": { "SymbolPath": "MAIN.ErrorHandler.aHmiAlarms", "CycleTimeMs": 200, "PlcClock": "Local" }
    }
  }
}
```

A target absent from `PlcAlarms:Targets` is simply not monitored — the package is opt-in per target. Every misconfiguration (a `plcId` with no matching entry under `PlcTargets`, a missing `SymbolPath`, a non-positive `CycleTimeMs`) is reported at startup, all at once. A relative `TextCatalog` resolves against the host's content root, so `"alarms.json"` means the file next to `appsettings.json` whatever the working directory happens to be.

```csharp
builder.Services
    .AddTwinCatAds(builder.Configuration)
    .AddTwinCatAdsAlarms(builder.Configuration);

var app = builder.Build();
var monitor = app.Services.GetRequiredService<IPlcAlarmMonitor>();

// Attach handlers BEFORE starting the host: the monitor is a hosted service, so it
// registers its subscription during startup and the PLC's first snapshot follows
// immediately — a handler attached afterwards can miss it.
monitor.AlarmChanged += (_, t) => Console.WriteLine($"{t.Kind}: {t.Alarm.Key}");

await app.StartAsync();

// Only meaningful once the host is running. Before StartAsync nothing is subscribed, so
// GetOutstanding() is empty and AcknowledgeAsync finds no such alarm and returns false.
foreach (var alarm in monitor.GetOutstanding())
    Console.WriteLine($"{alarm.Key} ({alarm.Severity}): {alarm.Text}");

await monitor.AcknowledgeAsync("plc1", "BMK1Err404", CancellationToken.None);

await app.WaitForShutdownAsync();
```

**An alarm is outstanding while its fault is present OR it still awaits acknowledgement.** A PLC alarm array is fixed-size with permanent slots — an alarm ends by `IsActive := FALSE`, never by leaving the array — and when `NeedsAck` is set the entry outlives its fault condition until an operator acknowledges it. So a fault that self-clears before anyone looks still reaches you, `Cleared` marks the fault ending while the alarm stays outstanding, and `Ended` fires only once the alarm is genuinely finished.

**Identity is `sKey`, not `Id`.** `Id` names the equipment (BMK) and is shared by every alarm on that machine; `sKey` is the PLC's own composite key combining the equipment identifier and the error code — `Test_Err_60` for equipment `Test`, error code `60`, for example. The exact spelling is the PLC program's business and this package treats it as opaque: never parse it. Group by `EquipmentId`, key by `Key`.

**Acknowledgement is a method call on the PLC, not a write to the entry.** `AcknowledgeAsync` finds the alarm by `Key` among the outstanding set, then calls the PLC method named by `AcknowledgeMethod` (default `AcknowledgeAlarm`) on the function block that owns acknowledgement, passing that key. The block's instance path is derived by trimming the last segment off `SymbolPath` — `MAIN.ErrorHandler.aHmiAlarms` derives `MAIN.ErrorHandler` — which is right when the alarm array is a member of that block and wrong for every other layout; set `AcknowledgeInstancePath` for those. The returned `deaReturnType` is resolved **by name**, not by number: `SUCCESS` returns `true`, `NOT_FOUND` returns `false`, and any other member raises `PlcAlarmAcknowledgeException` naming it. Nothing is written to the array entry — on hardware the array is a read-only projection that accepts an `IsAcked` write and discards it, which is exactly why acknowledgement goes through the block instead. Naming the alarm by key also means no slot is addressed, so there is no window in which the acknowledgement could land on whatever alarm has since taken that slot. A vendor whose PLC acknowledges differently registers its own `IPlcAlarmDialect`; the shipped one is simply the default. Registering one before `AddTwinCatAdsAlarms` also takes the shipped dialect's configuration rules out of startup validation, so a PLC that needs no instance path is no longer asked for one.

**The PLC method needs `{attribute 'TcRpcEnable'}`.** A method without it is not reachable over ADS at all, and the call fails as an unknown method however correct the instance path and the name are. When acknowledgement raises `AdsErrorException` on a target whose alarms otherwise arrive normally, check the attribute on the method declaration first — it is the likeliest cause and the cheapest to rule out.

**Every restart re-raises everything outstanding.** The outstanding set lives in memory and starts empty, and ADS delivers one notification on registration — so the first snapshot after a host restart reports every alarm the PLC is currently holding as newly `Raised`. There is no way to tell a two-day-old alarm from one raised while the host was down without persisting the set. Forwarding `Raised` straight to a pager therefore pages the whole outstanding set on every deployment: deduplicate downstream on `Key` plus `PlcTimestamp`, or suppress the first snapshot after start.

**A PLC that is down at boot does not fail the host.** Its first subscription attempt cannot succeed, so it is logged at `Error` and retried automatically the next time that target's connection reports `Connected` — every other target monitors normally in the meantime, and the offline one simply reports no alarms until it comes back. Once a target is registered the core library's durable subscriptions take over and carry it across later reconnects. Registration is attempted for all targets concurrently, so ten offline PLCs cost one connection timeout, not ten. Only unreachability is treated this way: a `SymbolPath` the PLC does not have is a configuration fault that no reconnect will fix, and still fails startup — **provided that target answered at boot**. If it did not, there is no startup left to fail: the bad path surfaces on the deferred retry instead, where it is logged at `Error` and re-attempted on every reconnect, forever. Watch the log for a target that never reports `Monitoring alarms on …`.

**Transitions for one target arrive in the order they were computed**, so a consumer folding the stream into its own state never sees a `Raised` land after the `Ended` that followed it. That ordering is bought by holding the target's lock across delivery, which means **a handler that blocks delays that target's next snapshot** — do the minimum on the notification thread and hand the work off. Ordering is per target, not across targets.

`AlarmChanged` and `Transitions` treat a throwing consumer differently, on purpose. `AlarmChanged` handlers are isolated from one another: a handler that throws is logged and the remaining handlers still receive that transition. `Transitions` is an ordinary `IObservable<AlarmTransition>` and follows the standard Rx contract — throwing from `OnNext` is an observer bug, and a subscriber that throws can starve the subscribers after it for that transition. Either way the exception never escapes onto the notification thread, and the next transition is still delivered.

**A shape mismatch is loud, but not fatal.** If the PLC's `ST_ErrorEntry` stops matching what the package binds, it throws `PlcAlarmShapeException` naming the member and the symbol path rather than degrading to defaults — a silently wrong alarm list is worse than an absent one. That snapshot is dropped whole and logged at `Error`, the outstanding set keeps its last good reading, and the subscription survives to recover on the next well-formed notification.

**A bad *value* is not a shape mismatch, and stays in its own slot.** That distinction is the difference between dropping one field and going blind: the alarm array is fixed-size with permanent slots, so an entry carrying nonsense arrives again on every notification, and treating it as a broken type would drop every snapshot for as long as it sat there. Two values are therefore kept rather than thrown on — an `ErrorType` outside `E_ErrorType`, which binds with its raw number, and a `PLCTimeStamp` that is not a real date, which reads as `default(DateTime)` exactly as a zeroed `TIMESTRUCT` already does. Each is logged once at `Warning` (per distinct value, and per slot, respectively) instead of on every cycle, and every other entry in the array binds normally. A *missing* or *retyped* member is still a shape mismatch and still behaves as above.

```csharp
builder.Services
    .AddHealthChecks()
    .AddTwinCatAdsHealthCheck()        // can each target be reached at all?
    .AddTwinCatAdsAlarmHealthCheck();  // how bad are the alarms it can see?
```

`AddTwinCatAdsAlarmHealthCheck()` reports Degraded at `Warning` and Unhealthy at `Error` by default, from the worst severity outstanding. **It reports only that** — it says nothing about whether monitoring is live, so a target still waiting for its first connection has no alarms and looks healthy here, indistinguishable from one that is connected and genuinely quiet. Register the core's `AddTwinCatAdsHealthCheck()` alongside it for per-target connectivity; the two together are the full picture.

See [`examples/Dahlke.TwinCAT.Ads.Examples.ErrorHandler`](examples/Dahlke.TwinCAT.Ads.Examples.ErrorHandler/) for a runnable demo that walks a whole alarm lifecycle in simulation.

## Connection Lookup

```csharp
// Always returns the stable facade for a configured target — never null.
// Throws UnknownPlcTargetException (listing configured ids) for an unknown id.
var conn = pool.GetConnection("plc1");

// Non-throwing variant:
if (!pool.TryGetConnection("plc1", out var conn))
    return Results.NotFound("Unknown PLC.");

// Enumerate all targets:
foreach (var (plcId, conn) in pool.GetAllConnections())
    Console.WriteLine($"{plcId} ({conn.DisplayName}) connected: {conn.IsConnected}");
```

## Connection State

```csharp
// Observational snapshot — a hint, not a guard.
bool up = conn.IsConnected;
ConnectionState state = conn.State; // Disconnected | Connecting | Connected

// Reactive notification:
conn.ConnectionStateChanged += (_, e) =>
    Console.WriteLine($"{e.PlcId}: {e.PreviousState} → {e.State}");
```

`IsConnected` and `State` are snapshots. Operation methods do not consult them; they apply the wait-then-throw contract directly.

`Connected` means the link has been **proven by a real ADS round trip** — the pool issues a `ReadState` probe after `Connect()` and only publishes the connection when the probe answers, then keeps it honest with a periodic health check. A locally "connected" socket whose peer is unreachable (dead route, misconfigured `AmsNetId`, cable pulled) reports `Disconnected`, not a false green.

For dashboards and status endpoints, the pool exposes a per-target snapshot:

```csharp
IReadOnlyList<PlcTargetStatus> states = pool.GetTargetStates();
// [ PlcTargetStatus { PlcId = "plc1", Mode = Real, State = Connected }, ... ]
```

## Wait-then-Throw Semantics

When no live connection is available (connecting, mid-outage), every operation on an `IAdsConnection` waits up to the target's `TimeoutMs` milliseconds for a connection to be published, then throws `AdsConnectionUnavailableException`. After the pool is stopped (host shutdown), operations fail fast without waiting.

`TimeoutException` is thrown when the hardware round-trip exceeds `TimeoutMs`. `OperationCanceledException` is thrown only when the caller's `CancellationToken` fires. The two are never conflated.

## Simulation Mode

### `AddTwinCatAdsSimulation` — all targets forced to simulation

```csharp
// All targets are in-memory; no TwinCAT installation required.
builder.Services.AddTwinCatAdsSimulation(builder.Configuration);
```

### Per-target simulation — mixed fleets

```csharp
builder.Services.AddTwinCatAds(o =>
{
    o.Targets["real-plc"] = new PlcTargetOptions
    {
        AmsNetId = "192.168.1.10.1.1",
        Mode = ConnectionMode.Real,
    };
    o.Targets["sim-plc"] = new PlcTargetOptions
    {
        DisplayName = "Simulated PLC",
        Mode = ConnectionMode.Simulated,
        InitialValues = { ["GVL.Temp"] = 21.5f },
    };
});
```

### Seeding initial values

`InitialValues` are applied at connection creation. Writes fire subscriptions on changed values; `SetInitialValues` seeds the store silently without triggering callbacks.

In code-first configuration values keep their CLR types and are seeded verbatim. JSON configuration is string-typed, so a bare scalar entry is seeded as a `string` — a metadata read reports it as `STRING` where a real PLC would report `DINT`. Declare the PLC type to get a faithful stand-in:

```jsonc
"PlcTargets": {
  "sim-plc": {
    "Mode": "Simulated",
    "InitialValues": {
      "MAIN.Speed":    { "value": 1500, "type": "DINT"  },
      "MAIN.Setpoint": { "value": 21.5, "type": "LREAL" },
      "MAIN.Running":  { "value": true, "type": "BOOL"  },
      "MAIN.Cycle":    { "type": "TIME" },        // no value → the type's default
      "MAIN.Station":  "Demo Station"             // bare scalar → seeded as STRING
    }
  }
}
```

| Symbol | `ReadValueWithMetadataAsync` |
|---|---|
| `MAIN.Speed` | `1500` / `DINT` |
| `MAIN.Setpoint` | `21.5` / `LREAL` |
| `MAIN.Running` | `true` / `BOOL` |
| `MAIN.Station` | `"Demo Station"` / `STRING` |

`type` is any IEC 61131-3 elementary type name (`BOOL`, `BYTE`, `WORD`, `DWORD`, `LWORD`, `SINT`, `INT`, `DINT`, `LINT`, `USINT`, `UINT`, `UDINT`, `ULINT`, `REAL`, `LREAL`, `TIME`, `DT`, `STRING`, `WSTRING`), matched case-insensitively with Beckhoff aliases resolved. The type is never inferred from the value's content, so a `STRING` symbol holding `"1500"` stays a string. An unknown type, an unconvertible value, or a `value` with no `type` fails options validation at startup with every bad entry listed at once.

### Test-code direct access to `SimulatedAdsConnection`

```csharp
if (pool.TryGetSimulatedConnection("plc1", out var sim))
    sim.SetInitialValues(new Dictionary<string, object?> { ["GVL.A"] = 99 });
```

## Raw ADS channels

For targets the symbol API cannot reach — EtherCAT masters and slaves, the TwinCAT system service — inject `IAdsRawChannelFactory` and address them by index group and index offset.

```csharp
public sealed class EtherCatDiagnostics(IAdsRawChannelFactory channels)
{
    public async Task<ushort> ReadSlaveStateAsync(
        string masterNetId, ushort slaveAddress, CancellationToken ct)
    {
        var channel = channels.Get(masterNetId, 0xFFFF);

        var buffer = new byte[2];
        await channel.ReadAsync(0x0009, slaveAddress, buffer, ct);
        return BitConverter.ToUInt16(buffer);
    }
}
```

Channels are cached per `(amsNetId, port)` and **never disposed by you** — hold the reference as long as you like. `Get` never blocks and never throws for a present Net ID, however malformed; an unreachable target simply reports `Disconnected` until you operate on it. Only a `null` Net ID throws (`ArgumentNullException`), because that is a caller bug rather than a target that happens not to exist.

The Net ID is trimmed and canonicalised before it is used as a key, so `"1.2.3.4.5.6"`, `"01.2.3.4.5.6"` and `" 1.2.3.4.5.6"` are one channel. An octet outside 0–255 is **zeroed rather than rejected** — `Get("999.1.1.1.1.1", 851)` addresses `0.1.1.1.1.1`, and a warning is logged once per distinct spelling — because that is how the ADS stack resolves the address on the wire. Configured Net IDs — targets, routes, the router's own and seed entries — are stricter and reject the same value at startup; see [Simulation](#simulation).

Raw channels are unrelated to `PlcTargets`: they need no configured target, and `RawChannels:Seed` names targets only to pre-load their simulated contents, never to declare which ones are reachable. Because a real raw channel cannot route without the embedded AMS router, leaving `RawChannels.Mode` at its default `Real` starts the router even when every configured PLC target is simulated. `AddTwinCatAdsSimulation` forces `RawChannels.Mode` to `Simulated` so it never does.

### What to expect when things go wrong

| Situation | Result |
|---|---|
| Device answers with an error code | `AdsErrorException` — an answer, never retried |
| Every attempt timed out | `TimeoutException` |
| You cancelled | `OperationCanceledException` carrying your token; no further attempt is made |
| Transport could not be opened, or the host has shut down | `AdsConnectionUnavailableException` |

The timeout bounds **each attempt**, so the default 5000 ms with one retry can take 10 seconds before throwing — the worst case for the attempts is `TimeoutMs × (RetryCount + 1)`. Pass an explicit `TimeSpan` overload when you need a tighter bound: probing a slave that may have no mailbox, for instance. A call that also has to build the transport for a channel with live subscriptions waits for that rebuild's restore pass first, which re-registers those subscriptions one at a time under their own separate bounds; that wait is not contained by the call's own attempt bound.

The table covers the cases the contract names, not every exception the runtime can produce.

### Notifications

```csharp
var handle = await channel.SubscribeAsync(
    indexGroup: 0x0009, indexOffset: slaveAddress, length: 2, cycleTimeMs: 200,
    handler: data => Console.WriteLine(BitConverter.ToUInt16(data)),
    ct);
```

The handler receives a `ReadOnlySpan<byte>` valid only for that call — copy with `data.ToArray()` if you need to keep it. The compiler will not let you store the span itself, which is the point; the cost is that a handler cannot be `async`. Against a real target the handler runs on the ADS notification thread, never the caller's, so it must be thread-safe and must not block. A handler that throws is logged at Warning and keeps its subscription.

Registration is bounded by `TimeoutMs` and is deliberately **never retried** — a retry would mean dropping and rebuilding the transport, re-registering every *other* subscription on the channel as a side effect of one subscriber's retry.

Subscriptions survive a transport drop and are re-registered automatically, exactly once, while your handle stays valid. A live subscription pins its channel against idle eviction, so **dispose the handle** when you are done — dropping it on the floor holds a connection open for the factory's lifetime. After host shutdown no transport is rebuilt, so a live subscription simply goes quiet rather than raising anything.

### Simulation

Raw-channel options bind from the `RawChannels` section, alongside `PlcTargets` and `AmsRouter`:

```jsonc
{
  "RawChannels": {
    "Mode": "Simulated",
    "Seed": [
      {
        "AmsNetId": "192.168.1.10.3.1",
        "Port": 65535,
        "Slots": [
          { "IndexGroup": "0x11", "IndexOffset": 1001, "Bytes": "02000000410C0000" }
        ]
      }
    ]
  }
}
```

`Seed` is an **array of objects** rather than a dictionary keyed on `amsNetId:port`, because `:` is the configuration hierarchy separator — such a key flattens into nested sections and loses everything after the Net ID.

The same options are settable in code, which is what a host with no configuration file wants:

```csharp
builder.Services.AddTwinCatAds(o =>
{
    o.RawChannels.Mode = ConnectionMode.Simulated;
    o.RawChannels.Seed.Add(new AdsRawChannelSeed
    {
        AmsNetId = "192.168.1.10.3.1",
        Port = 65535,
        Slots = [new AdsRawChannelSeedSlot
        {
            IndexGroup = "0x11", IndexOffset = "1001", Bytes = "02000000410C0000",
        }],
    });
});
```

`IndexGroup` and `IndexOffset` accept decimal or `0x`-prefixed hex, which is why they are strings. A seed entry's Net ID is matched against the channel **after normalisation**, so `01.2.3.4.5.6` still seeds the channel for `1.2.3.4.5.6`.

> **Note.** `AddTwinCatAdsSimulation` forces `Mode` to `Simulated` *after* binding, so a host that sets `"Mode": "Real"` in configuration and calls that helper still gets simulation.

Seeding is also available at runtime, which is what tests and demo hosts usually want:

```csharp
if (channels.TryGetSimulated("192.168.1.10.3.1", 0xFFFF, out var sim))
    sim.Seed(0x11, 1001, [0x02, 0x00, 0x00, 0x00]);
```

The store knows nothing about EtherCAT, CoE or files — seed the bytes your own decoder expects. An unseeded read answers `DeviceInvalidOffset`, the same code real hardware gives, so your error handling is exercised too. `ReadWriteAsync` in simulation writes the source to the slot and returns the slot's bytes; it will never invent a file handle, so seed the response you expect.

Simulated subscriptions ignore `cycleTimeMs` and fire on every write made *through the channel* to the watched slot, with no coalescing, on whichever thread performed the write. `Seed` writes the slot **without** firing — it arranges state rather than reporting a change.

Seed entries are validated at startup in **both** modes, so a malformed entry left behind after switching to `Real` still fails the host instead of sitting silently broken. A seed entry's AMS Net ID must be six dot-separated octets each in 0–255 — stricter than `Get`, deliberately, because a declaration's typo has no correct reading.

## Health Check

```csharp
builder.Services
    .AddTwinCatAds(builder.Configuration);

builder.Services
    .AddHealthChecks()
    .AddTwinCatAdsHealthCheck(); // name defaults to "twincat_ads"

var app = builder.Build();
app.MapHealthChecks("/health");
```

Returns `Healthy` when every target is connected, `Degraded` when some — but not all — targets are connected (a disconnected simulated target degrades health too), and `Unhealthy` when no target is connected (including the case where real targets are still waiting on the router). The response includes per-target data.

## Configuration Reference

### `AmsRouter` section (optional)

| Key | Type | Description |
|-----|------|-------------|
| `NetId` | `string` | AMS Net ID for the embedded TCP/IP router: six dot-separated octets each in 0–255 (e.g. `127.0.0.1.1.1`). Enforced **strictly** — `999.1.1.1.1.1` fails the host rather than being laundered to `0.1.1.1.1.1`. Omit to use the system TwinCAT router. |
| `Routes` | `List<AmsRouteOptions>` | Remote routes the embedded router adds once it has started — see below. Empty by default. Validated at startup whether or not the embedded router is enabled |

Each `Routes` entry:

| Property | Type | Default | Description |
|-----|------|---------|-------------|
| `Name` | `string` | `""` | Required, and unique within `Routes`. The router keys its route table by name |
| `NetId` | `string` | `""` | Required. The remote device's AMS Net ID: six dot-separated octets each in 0–255. Enforced **strictly** — `999.1.1.1.1.1` fails the host rather than being laundered to `0.1.1.1.1.1` |
| `Address` | `string` | `""` | Required. The device's IP address (`192.168.1.223`) or host name (`cx-01a2b3`); either is resolved by the router |

**Without a route, a host with no TwinCAT installation cannot reach a remote PLC at all** — every
operation answers `TargetMachineNotFound`. This is invisible on Windows, where the OS router
already holds the routes, and it is why `Routes` exists rather than a `StaticRoutes` key: no key
under `AmsRouter` reaches Beckhoff's route table (four spellings were measured yielding zero
routes), and its only file source is a TwinCAT `StaticRoutes.xml` on disk.

```json
{
  "AmsRouter": {
    "NetId": "192.168.1.220.1.1",
    "Routes": [
      { "Name": "rack", "NetId": "5.138.44.199.1.1", "Address": "192.168.1.223" }
    ]
  }
}
```

Entries are added **after** the router has started and **before** the readiness signal releases
the connection pool, so a pool connection never races a missing route. A route the router rejects
is logged at `Warning` naming it rather than throwing — one unreachable device must not cost every
reachable one.

### `PlcTargets` section

Each key is a PLC identifier used with `GetConnection(plcId)`.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `AmsNetId` | `string` | — | AMS Net ID of the PLC (required for `Real` targets): six dot-separated octets each in 0–255. Enforced **strictly** — `999.1.1.1.1.1` fails the host rather than being laundered to `0.1.1.1.1.1` |
| `Port` | `int` | `851` | ADS port number |
| `DisplayName` | `string` | `""` | Human-readable name for logging |
| `TimeoutMs` | `int` | `5000` | Per-operation timeout in milliseconds. Must be greater than zero |
| `SymbolBrowseTimeoutMs` | `int` | `30000` | Timeout for `GetSymbolsAsync` / `SearchSymbolsAsync`, which upload the PLC's symbol table and take far longer than a single read. Must be greater than zero |
| `Mode` | `ConnectionMode` | `Real` | `Real` or `Simulated` |
| `InitialValues` | `Dictionary<string, object?>` | `{}` | Symbol seed values for simulated targets. A bare scalar seeds a `string`; `{ "value": …, "type": "DINT" }` seeds the declared PLC type — see [Seeding initial values](#seeding-initial-values) |

### `RawChannels` section

Global policy for [raw ADS channels](#raw-ads-channels). There is nothing per-target to configure, because a raw channel addresses whatever AMS target the caller names.

| Property | Type | Default | Description |
|-----|------|---------|-------------|
| `Mode` | `ConnectionMode` | `Real` | `Real` or `Simulated`. `Real` starts the embedded AMS router even when every `PlcTargets` entry is simulated |
| `TimeoutMs` | `int` | `5000` | Timeout for each **attempt**, not for the retry sequence. Must be greater than zero |
| `RetryCount` | `int` | `1` | Retries after a failed attempt, so `1` means up to two attempts. Must not be negative. Applies only to a timeout with no device answer |
| `IdleEvictionMs` | `int` | `60000` | How long a channel may go unused before its transport is disposed. Must be greater than zero. A channel with a live subscription is never evicted |
| `Seed` | `List<AdsRawChannelSeed>` | `[]` | Simulation seed data — see below. Validated at startup in **both** modes |

Each `Seed` entry:

| Property | Type | Default | Description |
|-----|------|---------|-------------|
| `AmsNetId` | `string` | `""` | Six dot-separated octets each in 0–255. Matched against a channel after normalisation, so `01.2.3.4.5.6` seeds `1.2.3.4.5.6` |
| `Port` | `int` | `0` | ADS port, 0–65535. Decimal or `0x`-prefixed hex — `65535` and `"0xFFFF"` both bind. **`"0x851"` is 2129, not 851** — the canonical TC3 runtime port is decimal `851` |
| `Slots` | `List<AdsRawChannelSeedSlot>` | `[]` | The slots to pre-load. An entry with none declares a reachable but empty target |

Each `Slots` entry:

| Property | Type | Default | Description |
|-----|------|---------|-------------|
| `IndexGroup` | `string` | `""` | Decimal (`17`) or `0x`-prefixed hex (`0x11`). A string because configuration cannot express the hex form as a number |
| `IndexOffset` | `string` | `""` | Same form as `IndexGroup` |
| `Bytes` | `string` | `""` | Hex payload, optional `0x` prefix, even number of digits |

The Net ID here is validated **more strictly than `IAdsRawChannelFactory.Get`**, which accepts an out-of-range octet and resolves it the way the ADS stack does. A seed entry is a declaration whose typo has no correct reading, so `999.1.1.1.1.1` fails the host at startup rather than silently seeding `0.1.1.1.1.1`.

An entry or slot the configuration binder cannot bind also fails the host at startup. This has to be caught deliberately: the binder reports a bad *scalar* by throwing, but silently **discards a collection element** it cannot bind. Two mistakes hit that path — a `Port` that is not a number (`"Port": "typo"` drops the whole entry) and a slot written as a bare value instead of an object (`"Slots": [ "0x11", "0x12" ]` drops every slot). Either would otherwise leave the target reachable but unseeded, so every read answers an ADS error and a configuration mistake looks like a device fault.

### `AdsSymbolDump` section (optional diagnostics)

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Enabled` | `bool` | `false` | Dump symbol tree to the log at startup |
| `MaxDepth` | `int` | `1` | Maximum traversal depth (`0` = unlimited) |
| `Prefixes` | `string[]` | `[]` | Filter to symbols matching these prefixes |

The legacy `AdsSymbolTreeDump: true` key is still honoured; `AdsSymbolDump` takes precedence when both are present.

### `PlcAlarms` section (optional, `Dahlke.TwinCAT.Ads.Alarms`)

Read by `AddTwinCatAdsAlarms` — see [PLC alarms](#plc-alarms).

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `TextCatalog` | `string?` | `null` | Path to a JSON file mapping `sKey` to text. **A relative path resolves against the host's content root** — the directory holding `appsettings.json` — not the process working directory; an absolute path is used as written. A sibling `<name>.<culture>.json` is preferred per key, falling back to the neutral file. Omit for no catalog; a configured path that cannot be read fails startup |
| `Targets` | `Dictionary<string, PlcAlarmTargetOptions>` | `{}` | Per-target alarm settings, keyed by the same target id as `PlcTargets`. A target absent here is not monitored |

Each `Targets` entry:

| Property | Type | Default | Description |
|-----|------|---------|-------------|
| `SymbolPath` | `string` | `""` | Required. Fully-qualified path of the PLC's alarm array (e.g. `MAIN.ErrorHandler.aHmiAlarms`). Under the built-in dialect the last segment is trimmed off to derive `AcknowledgeInstancePath`, so a path with no parent segment fails startup **unless `AcknowledgeInstancePath` is set explicitly** — setting it satisfies the rule and nothing is derived. A custom `IPlcAlarmDialect` registered **before** `AddTwinCatAdsAlarms` derives nothing and the rule does not apply |
| `CycleTimeMs` | `int` | `200` | How often the PLC pushes array changes. Must be greater than zero |
| `PlcClock` | `PlcClockKind` | `Unspecified` | What clock the PLC's `TIMESTRUCT` runs on: `Unspecified`, `Utc` or `Local`. `TIMESTRUCT` carries no time zone, so this cannot be inferred — the default states no claim rather than stamping a wrong `DateTimeKind` on every alarm |
| `AcknowledgeInstancePath` | `string?` | `null` | Instance path of the function block that owns acknowledgement. Derived from `SymbolPath` when omitted, which is right only when the alarm array is a member of that block — set it for any other layout. Configures the built-in `FB_ErrorHandler` dialect; register a custom `IPlcAlarmDialect` **before** `AddTwinCatAdsAlarms` and neither this member nor the startup rule requiring it applies |
| `AcknowledgeMethod` | `string` | `"AcknowledgeAlarm"` | The PLC method that acknowledges one alarm by key. Must carry `{attribute 'TcRpcEnable'}` on the PLC to be reachable over ADS. Configures the built-in dialect, which also owns the rule that it be non-blank |

## IEC 61131-3 Type Mapping

`Iec61131Converter` is a table-driven utility that maps IEC 61131-3 elementary type names to and from .NET types, supplies typed default values, and converts boxed values — reusing the same invariant-culture conversion core as typed reads. It exposes two tiers:

- **`Iec61131Converter` (strict core)** — recognises only the canonical uppercase IEC names (`BOOL`, `DINT`, `LREAL`, …), matched case-sensitively. Use this when you require strict, standard names.
- **`Iec61131Converter.Beckhoff` (lenient tier)** — case-insensitive and alias-aware. It recognises mixed-case names and Beckhoff/non-standard aliases (`dtSystemTime` → `DT`, `T_UD` → `TIME`, `BIT`/`BIT8` → `BOOL`), normalises them to a canonical name, then delegates to the strict core.

```csharp
// Forward: IEC name -> .NET Type (strict, case-sensitive)
Type t = Iec61131Converter.GetDotNetType("DINT");        // typeof(int)

// Reverse: .NET Type -> canonical IEC name (deterministic)
string n = Iec61131Converter.GetIecTypeName(typeof(int)); // "DINT"

// Default value and conversion (invariant culture)
object? d = Iec61131Converter.GetDefaultValue("STRING");   // "" (never null)
object? v = Iec61131Converter.ConvertValue("LREAL", "3.14"); // 3.14 (double)

// Lenient tier: case-insensitive + Beckhoff aliases
Type b = Iec61131Converter.Beckhoff.GetDotNetType("dint");        // typeof(int)
Type s = Iec61131Converter.Beckhoff.GetDotNetType("dtSystemTime"); // typeof(DateTime)
```

The forward map is many-to-one: the bit-string types and unsigned-integer types share a .NET type (`BYTE` and `USINT` both → `byte`; `STRING` and `WSTRING` both → `string`). The reverse map is deterministic — an unsigned .NET integer resolves to the unsigned-integer IEC type (`byte` → `USINT`, never `BYTE`), and `string` resolves to `STRING`.

## Examples

Runnable projects live in [`examples/`](examples/) — all work out of the box in simulation mode, no PLC required:

- [`Dahlke.TwinCAT.Ads.Examples.Cli`](examples/Dahlke.TwinCAT.Ads.Examples.Cli/) — console app demonstrating typed reads, writes, batch operations, ADS state, and subscriptions
- [`Dahlke.TwinCAT.Ads.Examples.MinimalApi`](examples/Dahlke.TwinCAT.Ads.Examples.MinimalApi/) — ASP.NET Core minimal API exposing PLC symbols over HTTP with a health endpoint
- [`Dahlke.TwinCAT.Ads.Examples.Reactive`](examples/Dahlke.TwinCAT.Ads.Examples.Reactive/) — console app demonstrating Rx `IObservable` streams: typed/untyped value changes with operator composition, and merged connection-state across targets
- [`Dahlke.TwinCAT.Ads.Examples.ErrorHandler`](examples/Dahlke.TwinCAT.Ads.Examples.ErrorHandler/) — console app walking a whole PLC alarm lifecycle: two alarms on one machine, one clearing before it is acknowledged, and an acknowledgement that reaches the PLC through a simulated `AcknowledgeAlarm` method

## License

[Apache License 2.0](LICENSE)
