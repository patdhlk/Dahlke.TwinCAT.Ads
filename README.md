# Dahlke.TwinCAT.Ads

[![CI](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/actions/workflows/ci.yml/badge.svg)](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Dahlke.TwinCAT.Ads.svg)](https://www.nuget.org/packages/Dahlke.TwinCAT.Ads)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

A .NET library for TwinCAT ADS with durable connections, typed symbol access, simulation mode, and ASP.NET Core integration.

## Features

- **Typed reads and writes** — `ReadValueAsync<T>` / `WriteValueAsync<T>` with widening conversions; PLC structs, function blocks, unions and arrays bind onto records, classes and structs by member name
- **Typed symbol handles** — `PlcSymbol<T>` declares a symbol's path and .NET type once instead of at every call site
- **Stable connection facades** — one object per target whose identity never changes; reconnects are invisible; operations wait up to `TimeoutMs` for a connection, then throw — never a false green
- **Durable subscriptions** — survive reconnects automatically, with an optional Rx companion package surfacing them as `IObservable<T>` streams
- **PLC alarm tracking** — the optional Alarms package keeps a live set of outstanding alarms from a TwinCAT alarm array, streams transitions, and acknowledges through the PLC's own method over ADS
- **Batch operations, RPC and enum metadata** — ADS sum commands for single-round-trip batches; `InvokeRpcMethodAsync` and `GetEnumMembersAsync` for method calls and reading enum members by name
- **Raw ADS channels** — address any `(amsNetId, port)` by index group and offset for targets the symbol API cannot reach (EtherCAT, the TwinCAT system service)
- **Per-target simulation and a testing harness** — mixed real/simulated fleets, seedable values, and a `TestPlc` fixture that records what the code under test wrote; no TwinCAT installation required
- **Production hosting** — health checks, options validation that fails boot with actionable messages, an embedded AMS router with retry, and no-host support for console/WPF/WinForms

## Documentation

Long-form documentation lives in [`docs/`](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/docs/index.md):

| | |
|---|---|
| [Connections](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/docs/connections.md) | Pool, no-host builder, lookup, keyed injection, state, timeouts |
| [Reading and writing values](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/docs/values.md) | Typed access, struct binding, batches, RPC, browsing, IEC type mapping |
| [Subscriptions](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/docs/subscriptions.md) | Durable subscriptions and the Rx companion |
| [PLC alarms](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/docs/alarms.md) | The Alarms package end to end |
| [Raw ADS channels](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/docs/raw-channels.md) | Index-group/offset access, notifications, simulation store |
| [Simulation](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/docs/simulation.md) | Per-target and whole-fleet simulation, seeding |
| [Testing](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/docs/testing.md) | `TestPlc`, write assertions, hand-written doubles |
| [Configuration reference](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/docs/configuration.md) | Every section, every default, every validation rule |
| [Troubleshooting](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/docs/troubleshooting.md) | **Indexed by the error text you see**, likeliest cause first |

The **[API reference](https://patdhlk.com/Dahlke.TwinCAT.Ads/)** is generated from the XML
documentation of every public member.

## Installation

```bash
dotnet add package Dahlke.TwinCAT.Ads
```

Every package targets `net8.0`, `net9.0`, `net10.0` and `netstandard2.0` — the last one for the
.NET Framework 4.8 WinForms/WPF HMIs and in-house tooling that TwinCAT shops keep in service for
decades. The CI test suites run on .NET Framework 4.8 against the `netstandard2.0` assemblies,
so that target is tested, not merely compiled.

Three optional companions, each depending on the core package:

```bash
dotnet add package Dahlke.TwinCAT.Ads.Reactive   # IObservable<T> streams — see docs/subscriptions.md
dotnet add package Dahlke.TwinCAT.Ads.Alarms     # PLC alarm tracking — see docs/alarms.md
dotnet add package Dahlke.TwinCAT.Ads.Testing    # test harness (add to test projects; no test framework dependency)
```

### EtherCAT packages

This repository also ships three EtherCAT packages. They are documented on their own package pages rather than here, because only one of them is about ADS at all:

| Package | What it is |
|---|---|
| [`Dahlke.EtherCAT.Diagnostics`](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/src/Dahlke.EtherCAT.Diagnostics/README.md) | Master and slave diagnostics over ADS — topology, slave and port state, CRC and frame error counters, sync-unit faults, CoE reads and writes, a decoded CiA-402 drive statusword, and a change-event stream. Built on this library's [raw ADS channels](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/docs/raw-channels.md), which is the only way to reach an EtherCAT master: there are no PLC symbols for any of it. |
| [`Dahlke.EtherCAT.Esi`](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/src/Dahlke.EtherCAT.Esi/README.md) | An ESI (EtherCAT Slave Information) device catalogue — parses vendor ESI XML and resolves a vendor/product/revision triple to a device description. **Depends on no ADS or TwinCAT package**; it is XML, options and logging. Usable on its own with nothing but a folder of ESI files. |
| [`Dahlke.EtherCAT.Cia402`](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/src/Dahlke.EtherCAT.Cia402/README.md) | CiA-402 (DS402) drive profile decoders — statusword, controlword and modes of operation, as pure functions over integers. **Depends on nothing at all**, not even `Microsoft.Extensions.*`, so it decodes a drive word that arrived over CoE, SoE, CANopen or a log file equally well. |

```bash
dotnet add package Dahlke.EtherCAT.Diagnostics   # pulls Dahlke.TwinCAT.Ads, .Esi and .Cia402
dotnet add package Dahlke.EtherCAT.Esi           # standalone
dotnet add package Dahlke.EtherCAT.Cia402        # standalone, zero dependencies
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

**Reading a value:**

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

### Console, WPF and WinForms — no host required

```csharp
await using var pool = await AdsConnectionPoolBuilder
    .Create()
    .AddTarget("plc1", o => { o.AmsNetId = "192.168.1.10.1.1"; o.Port = 851; })
    .BuildAndStartAsync();

var conn = pool.GetConnection("plc1");
```

The same pool, the same registrations, no generic host — see
[Connections](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/docs/connections.md) for
waiting on a real target, companion packages and shutdown.

## Examples

Runnable projects live in [`examples/`](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/tree/main/examples) — all work out of the box in simulation mode, no PLC required:

- [`Dahlke.TwinCAT.Ads.Examples.Cli`](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/tree/main/examples/Dahlke.TwinCAT.Ads.Examples.Cli) — console app demonstrating typed reads, writes, batch operations, ADS state, and subscriptions
- [`Dahlke.TwinCAT.Ads.Examples.MinimalApi`](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/tree/main/examples/Dahlke.TwinCAT.Ads.Examples.MinimalApi) — ASP.NET Core minimal API exposing PLC symbols over HTTP with a health endpoint
- [`Dahlke.TwinCAT.Ads.Examples.Reactive`](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/tree/main/examples/Dahlke.TwinCAT.Ads.Examples.Reactive) — console app demonstrating Rx `IObservable` streams: typed/untyped value changes with operator composition, and merged connection-state across targets
- [`Dahlke.TwinCAT.Ads.Examples.ErrorHandler`](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/tree/main/examples/Dahlke.TwinCAT.Ads.Examples.ErrorHandler) — console app walking a whole PLC alarm lifecycle: two alarms on one machine, one clearing before it is acknowledged, and an acknowledgement that reaches the PLC through a simulated `AcknowledgeAlarm` method

## License

[Apache License 2.0](LICENSE)
