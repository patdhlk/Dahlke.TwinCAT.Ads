# Dahlke.TwinCAT.Ads

[![CI](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/actions/workflows/ci.yml/badge.svg)](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Dahlke.TwinCAT.Ads.svg)](https://www.nuget.org/packages/Dahlke.TwinCAT.Ads)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

A reusable TwinCAT ADS connection pool for .NET with automatic reconnection, health monitoring, simulation mode, and ASP.NET Core integration.

## Features

- **Connection pooling** — manage multiple PLC connections with a single `IAdsConnectionPool`
- **Automatic reconnection** — exponential backoff (2s–30s) with periodic health checks
- **Embedded ADS router** — optional TCP/IP router via `Beckhoff.TwinCAT.Ads.TcpRouter`
- **Simulation mode** — in-memory key-value store for offline development and testing
- **ASP.NET Core integration** — one-line DI registration with `IHostedService` lifecycle
- **Symbol subscriptions** — device notification callbacks for real-time PLC data

## Installation

```bash
dotnet add package Dahlke.TwinCAT.Ads
```

## Quick Start

### 1. Configure PLC targets in `appsettings.json`

```json
{
  "AmsRouter": {
    "NetId": "127.0.0.1.1.1"
  },
  "PlcTargets": {
    "plc1": {
      "AmsNetId": "192.168.1.10.1.1",
      "Port": 851,
      "DisplayName": "My PLC",
      "TimeoutMs": 5000
    }
  }
}
```

### 2. Register services

```csharp
// Real PLC connections
builder.Services.AddTwinCatAds(builder.Configuration);

// Or use simulation mode for offline development
builder.Services.AddTwinCatAdsSimulation(builder.Configuration);
```

### 3. Use the connection pool

```csharp
public class MyService(IAdsConnectionPool pool)
{
    public async Task ReadPlcData(CancellationToken ct)
    {
        var connection = pool.GetConnection("plc1");
        if (connection is null || !connection.IsConnected)
            return;

        var value = await connection.ReadValueAsync("GVL.MyVariable", ct);
        var state = await connection.GetAdsStateAsync(ct);
    }
}
```

### Command-line applications

Outside ASP.NET Core, use the generic host: register the services the same way, start the host so the pool establishes its connections, then resolve `IAdsConnectionPool`.

```csharp
using Dahlke.TwinCAT.Ads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddTwinCatAds(builder.Configuration);
// Or: builder.Services.AddTwinCatAdsSimulation(builder.Configuration);

using var host = builder.Build();
await host.StartAsync();

var pool = host.Services.GetRequiredService<IAdsConnectionPool>();
var connection = pool.GetConnection("plc1");

if (connection is not null && connection.IsConnected)
{
    var value = await connection.ReadValueAsync("GVL.MyVariable", CancellationToken.None);
    Console.WriteLine($"GVL.MyVariable = {value}");
}

await host.StopAsync();
```

## Examples

Runnable projects live in [`examples/`](examples/) — both work out of the box in simulation mode, no PLC required:

- [`Dahlke.TwinCAT.Ads.Examples.Cli`](examples/Dahlke.TwinCAT.Ads.Examples.Cli/) — console app demonstrating reads, writes, batch operations, ADS state, and subscriptions
- [`Dahlke.TwinCAT.Ads.Examples.MinimalApi`](examples/Dahlke.TwinCAT.Ads.Examples.MinimalApi/) — ASP.NET Core minimal API exposing PLC symbols over HTTP

## Configuration Reference

### `AmsRouter` section (optional)

| Key | Type | Description |
|-----|------|-------------|
| `NetId` | `string` | AMS Net ID for the embedded TCP/IP router. Omit to use the system TwinCAT router. |

### `PlcTargets` section

Each key is a PLC identifier used with `GetConnection(plcId)`.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `AmsNetId` | `string` | `""` | AMS Net ID of the PLC |
| `Port` | `int` | `851` | ADS port number |
| `DisplayName` | `string` | `""` | Human-readable name for logging |
| `TimeoutMs` | `int` | `5000` | Operation timeout in milliseconds |

## Simulation Mode

`AddTwinCatAdsSimulation` registers an in-memory connection pool that requires no TwinCAT installation. Values written via `WriteValueAsync` are stored and returned by subsequent `ReadValueAsync` calls. Useful for UI development and integration testing.

## License

[Apache License 2.0](LICENSE)
