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
- **ADS sum commands** — batch read and write execute as a single round-trip on real connections; per-symbol `AdsValueResult` for granular success/failure
- **Connection state observability** — `State` property (tri-state), `IsConnected` snapshot, `ConnectionStateChanged` event
- **Per-target simulation** — `ConnectionMode.Real | Simulated` per target; mixed fleets supported; `InitialValues` seeding; `AddTwinCatAdsSimulation` forces all targets to simulated
- **Health check** — Healthy / Degraded / Unhealthy with per-target data via `AddTwinCatAdsHealthCheck()`
- **Options validation at startup** — malformed AMS Net IDs, invalid ports, and non-positive timeouts fail boot with actionable messages
- **Embedded ADS router with retry** — retries with backoff (2 s → 30 s cap); pool startup never blocks on the router

## Installation

```bash
dotnet add package Dahlke.TwinCAT.Ads
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

On real connections both operations use a single ADS sum command (one round-trip). A per-symbol failure is captured in `AdsValueResult.Error` and does not abort the batch. A whole-batch timeout throws `TimeoutException`; caller cancellation throws `OperationCanceledException`.

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

`InitialValues` are applied at connection creation. In code-first configuration values keep their CLR types; values from JSON configuration arrive as strings (plan reads to handle `Convert.ChangeType` conversion). Writes fire subscriptions on changed values; `SetInitialValues` seeds the store silently without triggering callbacks.

### Test-code direct access to `SimulatedAdsConnection`

```csharp
if (pool.TryGetSimulatedConnection("plc1", out var sim))
    sim.SetInitialValues(new Dictionary<string, object?> { ["GVL.A"] = 99 });
```

## Health Check

```csharp
builder.Services
    .AddTwinCatAds(builder.Configuration);

builder.Services
    .AddHealthChecks()
    .AddTwinCatAdsHealthCheck(); // name defaults to "twincat_ads"

app.MapHealthChecks("/health");
```

Returns `Healthy` when every target is connected, `Degraded` when some — but not all — targets are connected (a disconnected simulated target degrades health too), and `Unhealthy` when no target is connected (including the case where real targets are still waiting on the router). The response includes per-target data.

## Configuration Reference

### `AmsRouter` section (optional)

| Key | Type | Description |
|-----|------|-------------|
| `NetId` | `string` | AMS Net ID for the embedded TCP/IP router. Omit to use the system TwinCAT router. |

### `PlcTargets` section

Each key is a PLC identifier used with `GetConnection(plcId)`.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `AmsNetId` | `string` | — | AMS Net ID of the PLC (required for `Real` targets) |
| `Port` | `int` | `851` | ADS port number |
| `DisplayName` | `string` | `""` | Human-readable name for logging |
| `TimeoutMs` | `int` | `5000` | Per-operation timeout in milliseconds |
| `Mode` | `ConnectionMode` | `Real` | `Real` or `Simulated` |
| `InitialValues` | `Dictionary<string, object?>` | `{}` | Symbol seed values for simulated targets |

### `AdsSymbolDump` section (optional diagnostics)

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Enabled` | `bool` | `false` | Dump symbol tree to the log at startup |
| `MaxDepth` | `int` | `1` | Maximum traversal depth (`0` = unlimited) |
| `Prefixes` | `string[]` | `[]` | Filter to symbols matching these prefixes |

The legacy `AdsSymbolTreeDump: true` key is still honoured; `AdsSymbolDump` takes precedence when both are present.

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

Runnable projects live in [`examples/`](examples/) — both work out of the box in simulation mode, no PLC required:

- [`Dahlke.TwinCAT.Ads.Examples.Cli`](examples/Dahlke.TwinCAT.Ads.Examples.Cli/) — console app demonstrating typed reads, writes, batch operations, ADS state, and subscriptions
- [`Dahlke.TwinCAT.Ads.Examples.MinimalApi`](examples/Dahlke.TwinCAT.Ads.Examples.MinimalApi/) — ASP.NET Core minimal API exposing PLC symbols over HTTP with a health endpoint

## License

[Apache License 2.0](LICENSE)
