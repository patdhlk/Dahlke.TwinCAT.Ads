# Examples

All examples run out of the box in **simulation mode** — no TwinCAT installation or PLC required. Switch to a real PLC by adjusting the flag described per example and pointing `PlcTargets` in `appsettings.json` at your hardware.

## Dahlke.TwinCAT.Ads.Examples.Cli

A console application using the generic host. Demonstrates typed reads and writes, batch operations with `AdsValueResult`, ADS state queries, typed subscriptions, and untyped subscriptions.

```bash
# Simulation mode (default)
dotnet run --project examples/Dahlke.TwinCAT.Ads.Examples.Cli

# Against a real PLC
dotnet run --project examples/Dahlke.TwinCAT.Ads.Examples.Cli -- --real
```

## Dahlke.TwinCAT.Ads.Examples.MinimalApi

An ASP.NET Core minimal API exposing PLC symbols over HTTP. Set `"UseSimulation": false` in `appsettings.json` to use a real PLC.

```bash
dotnet run --project examples/Dahlke.TwinCAT.Ads.Examples.MinimalApi
```

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/health` | TwinCAT ADS health check (Healthy / Degraded / Unhealthy) |
| `GET` | `/plcs` | List all PLC connections and their status |
| `GET` | `/plcs/{plcId}/state` | Current ADS state (`Run`, `Stop`, ...) |
| `GET` | `/plcs/{plcId}/symbols/{symbolPath}` | Read a symbol value |
| `PUT` | `/plcs/{plcId}/symbols/{symbolPath}` | Write a symbol value, body: `{"value": 42}` |
| `POST` | `/plcs/{plcId}/reconnect` | Force a reconnect |

```bash
# Write then read back a symbol (simulation stores whatever you write)
curl -X PUT localhost:5000/plcs/plc1/symbols/GVL.Counter \
     -H "Content-Type: application/json" -d '{"value": 42}'
curl localhost:5000/plcs/plc1/symbols/GVL.Counter

# Health check
curl localhost:5000/health
```

## Dahlke.TwinCAT.Ads.Examples.Reactive

A console application using the optional `Dahlke.TwinCAT.Ads.Reactive` package. Demonstrates typed and untyped value streams composed with Rx operators (`Where`, `DistinctUntilChanged`, `Throttle`) and merged connection-state across targets. Runs entirely in simulation.

```bash
dotnet run --project examples/Dahlke.TwinCAT.Ads.Examples.Reactive
```

## Dahlke.TwinCAT.Ads.Examples.ErrorHandler

Monitors a PLC alarm array with the optional `Dahlke.TwinCAT.Ads.Alarms` package and prints every transition — raised, acknowledged, cleared, reoccurred and ended — with text resolved from `alarms.json`. In simulation mode a background driver walks a scripted alarm lifecycle: two alarms on the same equipment, one of which clears before it is acknowledged and is then ended by an `AcknowledgeAsync` write back to the PLC. The driver stops the host when the script finishes; against a real PLC the example runs until Ctrl+C.

```bash
# Simulation mode (default)
dotnet run --project examples/Dahlke.TwinCAT.Ads.Examples.ErrorHandler

# Against a real PLC
dotnet run --project examples/Dahlke.TwinCAT.Ads.Examples.ErrorHandler -- --real
```

```text
[RAISED] BMK1Err404 (Error) — Conveyor 1: material jam at the infeed
[RAISED] BMK1Err500 (Warning) — Conveyor 1: drive overtemperature
[CLEARED] BMK1Err404 (Error) — Conveyor 1: material jam at the infeed
Outstanding after the fault cleared:
  BMK1Err404 on BMK1 (Error) active=False acknowledged=False
  BMK1Err500 on BMK1 (Warning) active=True acknowledged=False
AcknowledgeAsync("BMK1Err404") -> True
[ACKNOWLEDGED] BMK1Err404 (Error) — Conveyor 1: material jam at the infeed
[ENDED] BMK1Err404 (Error) — Conveyor 1: material jam at the infeed
```

`BMK1Err404` stays outstanding after its fault clears because it still awaits acknowledgement — that is the point of the run. The `--real` mode is worth trying with the PLC switched off: the host starts anyway, logs that alarm monitoring could not be registered, and registers it when the target comes up.
