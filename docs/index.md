# Dahlke.TwinCAT.Ads documentation

A .NET library for TwinCAT ADS with durable connections, typed symbol access, simulation mode,
and ASP.NET Core integration. Install and quick start are in the
[README](https://github.com/patdhlk/Dahlke.TwinCAT.Ads#readme); this is the long-form
documentation.

**Something is failing and you have the error text?** Start at
[Troubleshooting](troubleshooting.md) — it is indexed by the error you see, likeliest cause
first.

| Page | What it covers |
|---|---|
| [Connections](connections.md) | The connection pool, the no-host builder for console/WPF/WinForms, connection lookup and keyed injection, connection state, wait-then-throw semantics, and per-call timeouts with `WithTimeout` |
| [Reading and writing values](values.md) | Typed reads and writes, binding PLC structs to .NET types, `PlcSymbol<T>` handles, batch operations, RPC method calls, symbol browsing, and the IEC 61131-3 type mapping |
| [Subscriptions](subscriptions.md) | Durable value-change subscriptions, notification metadata, and the Rx companion package |
| [PLC alarms](alarms.md) | The `Dahlke.TwinCAT.Ads.Alarms` package: tracking a TwinCAT alarm array, transition handlers, acknowledgement over ADS, restart semantics, and fault tolerance |
| [Raw ADS channels](raw-channels.md) | Index-group/index-offset access for targets the symbol API cannot reach — EtherCAT masters, the system service — with durable notifications and a seedable simulation store |
| [Simulation](simulation.md) | Per-target and whole-fleet simulation, seeding initial values from code and configuration |
| [Testing](testing.md) | The `Dahlke.TwinCAT.Ads.Testing` harness (`TestPlc`, write assertions) and hand-written doubles via `AdsConnectionBase` |
| [Configuration reference](configuration.md) | Every configuration section — `AmsRouter`, `PlcTargets`, `RawChannels`, `AdsSymbolDump`, `PlcAlarms` — with defaults and validation rules, plus health checks |
| [Troubleshooting](troubleshooting.md) | Indexed by the error text you see, likeliest cause first |

The **API reference**, generated from the XML documentation of every public member, is published
at [patdhlk.com/Dahlke.TwinCAT.Ads](https://patdhlk.com/Dahlke.TwinCAT.Ads/).

The three EtherCAT packages document themselves on their own package pages:
[`Dahlke.EtherCAT.Diagnostics`](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/src/Dahlke.EtherCAT.Diagnostics/README.md),
[`Dahlke.EtherCAT.Esi`](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/src/Dahlke.EtherCAT.Esi/README.md) and
[`Dahlke.EtherCAT.Cia402`](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/src/Dahlke.EtherCAT.Cia402/README.md).
