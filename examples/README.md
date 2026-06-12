# Examples

Both examples run out of the box in **simulation mode** — no TwinCAT installation or PLC required. Switch to a real PLC by adjusting the flag described per example and pointing `PlcTargets` in `appsettings.json` at your hardware.

## Dahlke.TwinCAT.Ads.Examples.Cli

A console application using the generic host. Demonstrates connection listing, single and batch reads/writes, ADS state queries, and symbol subscriptions.

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
```
