# Simulation

Every target can run against an in-memory value store instead of hardware — per target, or forced
for the whole fleet. No TwinCAT installation is required in simulation. For the test harness that
builds on this (`TestPlc`, hand-written doubles), see [Testing](testing.md).

## `AddTwinCatAdsSimulation` — all targets forced to simulation

```csharp
// All targets are in-memory; no TwinCAT installation required.
builder.Services.AddTwinCatAdsSimulation(builder.Configuration);
```

## Per-target simulation — mixed fleets

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

## Seeding initial values

`InitialValues` are applied at connection creation. Writes fire subscriptions on changed values;
`SetInitialValues` seeds the store silently without triggering callbacks.

Seed a struct-shaped symbol in code by seeding a member tree (or an instance of the type itself);
it then reads back as a .NET type — see
[Reading a PLC struct into a .NET type](values.md#reading-a-plc-struct-into-a-net-type):

```csharp
sim.SetInitialValues(new Dictionary<string, object?>
{
    ["MAIN.Motor"] = new Dictionary<string, object?> { ["Speed"] = 1500, ["Running"] = true },
});

MotorState motor = await sim.ReadValueAsync<MotorState>("MAIN.Motor");
```

**JSON `InitialValues` cannot seed a struct** — a config entry seeds one scalar symbol, so a
struct-shaped symbol has to be seeded in code.

In code-first configuration values keep their CLR types and are seeded verbatim. JSON
configuration is string-typed, so a bare scalar entry is seeded as a `string` — a metadata read
reports it as `STRING` where a real PLC would report `DINT`. Declare the PLC type to get a
faithful stand-in:

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

`type` is any IEC 61131-3 elementary type name (`BOOL`, `BYTE`, `WORD`, `DWORD`, `LWORD`, `SINT`,
`INT`, `DINT`, `LINT`, `USINT`, `UINT`, `UDINT`, `ULINT`, `REAL`, `LREAL`, `TIME`, `DT`, `STRING`,
`WSTRING`), matched case-insensitively with Beckhoff aliases resolved. The type is never inferred
from the value's content, so a `STRING` symbol holding `"1500"` stays a string. An unknown type,
an unconvertible value, or a `value` with no `type` fails options validation at startup with every
bad entry listed at once.

## Test-code direct access to `SimulatedAdsConnection`

```csharp
if (pool.TryGetSimulatedConnection("plc1", out var sim))
    sim.SetInitialValues(new Dictionary<string, object?> { ["GVL.A"] = 99 });
```

For a whole started fleet rather than one connection — seeded targets, write assertions, a pool
ready to inject — use the `Dahlke.TwinCAT.Ads.Testing` package instead: see
[Testing](testing.md).

## What simulation covers elsewhere

- **Subscriptions** fire on changed writes, exactly as [Subscriptions](subscriptions.md) describes.
- **Raw channels** have their own simulated store with byte-level seeding — see [Raw ADS channels](raw-channels.md#simulation).
- **Struct binding in simulation matches by name, hardware maps by declaration order** — the one gap simulation cannot close; see [the note under struct reads](values.md#reading-a-plc-struct-into-a-net-type).
