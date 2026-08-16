# Configuration reference

Every section this library binds, with defaults and validation rules. All options are also
settable in code — every `AddTwinCatAds` overload accepts a configuration delegate, and the
[no-host builder](connections.md#console-wpf-and-winforms--no-host-required) exposes the same
tree via `Configure`. The two compose:

```csharp
// Config binding runs first; the lambda layers on top.
builder.Services.AddTwinCatAds(builder.Configuration, o =>
{
    o.Diagnostics.SymbolDump.Prefixes.Add("GVL");
});
```

Options are validated at startup: malformed AMS Net IDs, invalid ports, non-positive timeouts and
malformed raw-channel seed entries fail boot with actionable messages, all at once, rather than
surfacing later as connection faults.

## `AmsRouter` section (optional)

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

The embedded router itself retries with backoff (2 s → 30 s cap); pool startup never blocks on the
router.

## `PlcTargets` section

Each key is a PLC identifier used with `GetConnection(plcId)`.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `AmsNetId` | `string` | — | AMS Net ID of the PLC (required for `Real` targets): six dot-separated octets each in 0–255. Enforced **strictly** — `999.1.1.1.1.1` fails the host rather than being laundered to `0.1.1.1.1.1` |
| `Port` | `int` | `851` | ADS port number |
| `DisplayName` | `string` | `""` | Human-readable name for logging |
| `TimeoutMs` | `int` | `5000` | Per-operation timeout in milliseconds. Must be greater than zero. Override it for individual calls with [`WithTimeout`](connections.md#per-call-timeouts--withtimeout) rather than raising it for the whole target |
| `SymbolBrowseTimeoutMs` | `int` | `30000` | Timeout for `GetSymbolsAsync` / `GetSymbolTreeAsync` / `SearchSymbolsAsync`, which upload the PLC's symbol table and take far longer than a single read. Must be greater than zero. Also overridden by `WithTimeout` |
| `Mode` | `ConnectionMode` | `Real` | `Real` or `Simulated` |
| `InitialValues` | `Dictionary<string, object?>` | `{}` | Symbol seed values for simulated targets. A bare scalar seeds a `string`; `{ "value": …, "type": "DINT" }` seeds the declared PLC type — see [Seeding initial values](simulation.md#seeding-initial-values) |

## `RawChannels` section

Global policy for [raw ADS channels](raw-channels.md). There is nothing per-target to configure,
because a raw channel addresses whatever AMS target the caller names.

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
| `Port` | `int?` | *(required)* | ADS port, 1–65535. Decimal or `0x`-prefixed hex — `65535` and `"0xFFFF"` both bind. **`"0x851"` is 2129, not 851** — the canonical TC3 runtime port is decimal `851` |
| `Slots` | `List<AdsRawChannelSeedSlot>` | `[]` | The slots to pre-load. An entry with none declares a reachable but empty target |

Each `Slots` entry:

| Property | Type | Default | Description |
|-----|------|---------|-------------|
| `IndexGroup` | `string` | `""` | Decimal (`17`) or `0x`-prefixed hex (`0x11`). A string because configuration cannot express the hex form as a number |
| `IndexOffset` | `string` | `""` | Same form as `IndexGroup` |
| `Bytes` | `string` | `""` | Hex payload, optional `0x` prefix, even number of digits |

The Net ID here is validated **more strictly than `IAdsRawChannelFactory.Get`**, which accepts an
out-of-range octet and resolves it the way the ADS stack does. A seed entry is a declaration whose
typo has no correct reading, so `999.1.1.1.1.1` fails the host at startup rather than silently
seeding `0.1.1.1.1.1`.

An entry or slot the configuration binder cannot bind also fails the host at startup. This has to
be caught deliberately: the binder reports a bad *scalar* by throwing, but silently **discards a
collection element** it cannot bind. Two mistakes hit that path — a `Port` that is not a number
(`"Port": "typo"` drops the whole entry) and a slot written as a bare value instead of an object
(`"Slots": [ "0x11", "0x12" ]` drops every slot). Either would otherwise leave the target
reachable but unseeded, so every read answers an ADS error and a configuration mistake looks like
a device fault.

**`Port` is `int?` rather than `int`, and required.** A seed entry names its target by Net ID
*and* port, so an entry without one matches no channel and seeds nothing. A non-nullable `int`
cannot express that: it defaults to `0`, and `0` is exactly what the binder produces for an entry
that never mentions a port — which since `Microsoft.Extensions.Configuration.Binder` 10.0.0
includes `"Port": null`, where through 9.x that was an unconvertible empty string that dropped the
entry and failed startup instead. The same `appsettings.json` therefore failed on .NET 8 and
started silently on .NET 10. Nullable, an absent key, a `null` and a `""` all arrive as "not
specified" on every framework and are rejected by name. Port `0` is likewise rejected now,
matching `PlcTargets:{id}:Port`, which has always required `[1, 65535]`.

## `AdsSymbolDump` section (optional diagnostics)

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Enabled` | `bool` | `false` | Dump symbol tree to the log at startup |
| `MaxDepth` | `int` | `1` | Maximum traversal depth (`0` = unlimited) |
| `Prefixes` | `string[]` | `[]` | Filter to symbols matching these prefixes |

The legacy `AdsSymbolTreeDump: true` key is still honoured; `AdsSymbolDump` takes precedence when
both are present.

## `PlcAlarms` section (optional, `Dahlke.TwinCAT.Ads.Alarms`)

Read by `AddTwinCatAdsAlarms` — see [PLC alarms](alarms.md).

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

## Health check

```csharp
builder.Services
    .AddTwinCatAds(builder.Configuration);

builder.Services
    .AddHealthChecks()
    .AddTwinCatAdsHealthCheck(); // name defaults to "twincat_ads"

var app = builder.Build();
app.MapHealthChecks("/health");
```

Returns `Healthy` when every target is connected, `Degraded` when some — but not all — targets are
connected (a disconnected simulated target degrades health too), and `Unhealthy` when no target is
connected (including the case where real targets are still waiting on the router). The response
includes per-target data.

The alarms package adds a second, severity-based check — see
[PLC alarms → Health checks](alarms.md#health-checks).
