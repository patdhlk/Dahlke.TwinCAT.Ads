# Troubleshooting

Indexed by what you actually see — the exception type, the error text, or the behaviour. Each
entry names the likeliest cause first, then the rarer ones, with a link to the long-form
explanation.

**Exceptions**

- [`TargetMachineNotFound` on every operation](#targetmachinenotfound-on-every-operation)
- [`DeviceSymbolNotFound` on one symbol](#devicesymbolnotfound-on-one-symbol)
- [`AdsConnectionUnavailableException`](#adsconnectionunavailableexception)
- [`TimeoutException`](#timeoutexception)
- [An RPC call fails as an unknown method](#an-rpc-call-fails-as-an-unknown-method)
- [A symbol browse times out at 5 s](#a-symbol-browse-times-out-at-5-s)
- [`UnknownPlcTargetException`](#unknownplctargetexception)
- [The host fails at startup with options validation errors](#the-host-fails-at-startup-with-options-validation-errors)
- [`PlcAlarmShapeException`](#plcalarmshapeexception)
- [`PlcAlarmAcknowledgeException` naming an enum member](#plcalarmacknowledgeexception-naming-an-enum-member)
- [A test assertion fails on a value that looks equal](#a-test-assertion-fails-on-a-value-that-looks-equal)
- [A raw-channel read answers `DeviceInvalidOffset` in simulation](#a-raw-channel-read-answers-deviceinvalidoffset-in-simulation)

**Wrong behaviour, no exception**

- [Alarms never arrive](#alarms-never-arrive)
- [Every alarm re-raises after a restart or deployment](#every-alarm-re-raises-after-a-restart-or-deployment)
- [`AcknowledgeAsync` returns `false`](#acknowledgeasync-returns-false)
- [A simulated symbol reports `STRING` where the PLC would report `DINT`](#a-simulated-symbol-reports-string-where-the-plc-would-report-dint)
- [A struct read fails naming one member](#a-struct-read-fails-naming-one-member)
- [A struct reads correctly in simulation and garbage on hardware](#a-struct-reads-correctly-in-simulation-and-garbage-on-hardware)
- [Enumerating keyed connections finds nothing — or throws](#enumerating-keyed-connections-finds-nothing--or-throws)
- [A subscription callback never fires](#a-subscription-callback-never-fires)
- [The health check is green but a target is not being monitored](#the-health-check-is-green-but-a-target-is-not-being-monitored)
- [Shutdown hangs](#shutdown-hangs)

---

## `TargetMachineNotFound` on every operation

**Likeliest cause: the machine running your application has no AMS route to the PLC.** On a host
without a TwinCAT installation the embedded router starts with an empty route table, and without a
route it cannot reach a remote PLC at all — every operation answers `TargetMachineNotFound`. Add
the route under `AmsRouter:Routes` ([configuration](configuration.md#amsrouter-section-optional)).
This is invisible on a Windows machine with TwinCAT installed, where the OS router already holds
the routes — which is why it classically appears the day the application moves to a container or
a fresh server.

Also check, in order:

- **The PLC has no return route to you.** AMS routing is bidirectional: the PLC's own route table
  (TwinCAT: *System → Routes*) must name your host's AMS Net ID (`AmsRouter:NetId`) and IP.
- **A wrong `AmsNetId` on the target.** The Net ID is not derived from the IP address; check it on
  the PLC under *System → Routes → NetId Management*.

## `DeviceSymbolNotFound` on one symbol

**Likeliest cause: the symbol path does not exist on that PLC** — a typo, or a PLC program change
that renamed or removed it. Nothing on the client side can check a path against the PLC, which is
why [`PlcSymbol<T>`](values.md#typed-symbol-handles) exists: one place per symbol to correct.

Also check, in order:

- **Wrong ADS port.** Port 851 is the first TC3 PLC runtime; a second runtime lives on 852. A
  valid path asked of the wrong runtime is simply not found.
- **The variable is not in the symbol table.** Local variables of a method, and variables excluded
  with `{attribute 'symbol' := 'none'}`, produce no symbol.
- **Browse before you guess:** [`SearchSymbolsAsync`](values.md#symbol-browsing-and-metadata) or
  the [`AdsSymbolDump` diagnostic](configuration.md#adssymboldump-section-optional-diagnostics)
  shows what the PLC actually exposes.

## `AdsConnectionUnavailableException`

**Likeliest cause: you read immediately after startup, before the target ever connected.**
`BuildAndStartAsync` (and host startup) return as soon as the pool is started; a real target
connects only once the embedded router is ready. An operation issued in that window waits
`TimeoutMs`, then throws exactly this. Gate startup reads with
[`WaitForConnectedAsync`](connections.md#waiting-for-a-real-target).

Also:

- **The target is mid-outage.** This is the [wait-then-throw
  contract](connections.md#wait-then-throw-semantics): the operation waited `TimeoutMs` for a
  connection and none was published. The connection facade stays valid; retry when
  `ConnectionStateChanged` reports `Connected`.
- **The host is shutting down.** After the pool stops, operations fail fast with this exception
  without waiting.

## `TimeoutException`

**Likeliest cause: the round-trip genuinely exceeded `TimeoutMs` — a slow symbol, a large struct
decode, or a congested network.** This exception means the target was *connected* and the
hardware did not answer in time (an unavailable connection throws
`AdsConnectionUnavailableException` instead; the two are never conflated). Scope a longer bound to
the slow call with [`WithTimeout`](connections.md#per-call-timeouts--withtimeout) rather than
raising `TimeoutMs` for the whole target.

On [raw channels](raw-channels.md#what-to-expect-when-things-go-wrong), remember the timeout
bounds each **attempt**: the default 5000 ms with `RetryCount: 1` takes up to 10 s before
throwing.

## An RPC call fails as an unknown method

**Likeliest cause: the PLC method is missing `{attribute 'TcRpcEnable'}`.** A method without it is
not reachable over ADS at all, and the call fails as an unknown method however correct the
instance path and the name are. This applies to your own
[`InvokeRpcMethodAsync`](values.md#plc-method-calls-and-enum-metadata) calls and equally to alarm
acknowledgement, which is an RPC under the hood: when `AcknowledgeAsync` raises
`AdsErrorException` on a target whose alarms otherwise arrive normally, check the attribute on the
method declaration first — it is the likeliest cause and the cheapest to rule out.

Also check, in order:

- **The instance path names the wrong function block.** For alarms, the block's path is derived by
  trimming the last segment off `SymbolPath`, which is wrong whenever the alarm array is not a
  member of the acknowledging block — set
  [`AcknowledgeInstancePath`](configuration.md#plcalarms-section-optional-dahlketwincatadsalarms).
- **The method name is misspelled** (`AcknowledgeMethod` for alarms, the `methodName` argument for
  your own calls). ADS reports a missing method the same way as an unexported one.

## A symbol browse times out at 5 s

**Likeliest cause: a `WithTimeout` scope built for quick reads is being reused for the browse.**
Browses are normally bounded by `SymbolBrowseTimeoutMs` (default 30 s, six times the default
`TimeoutMs`) precisely because uploading a symbol table is slow — but a
[`WithTimeout`](connections.md#per-call-timeouts--withtimeout) scope replaces **both** configured
bounds, so a 5 s scope silently shortens the browse to 5 s. Browse on the unscoped connection, or
give the browse its own scope.

Also:

- **The symbol table is genuinely huge.** A root `GetSymbolTreeAsync` projects every symbol on the
  PLC. Browse [one level at a time](values.md#symbol-browsing-and-metadata) with
  `GetSymbolsAsync(parentPath, includeChildren: false)`, or raise `SymbolBrowseTimeoutMs`.

## `UnknownPlcTargetException`

**The id you asked for is not configured** — the exception lists every id that is. Likeliest
cause: a spelling difference between the code and the `PlcTargets` key (ids are case-insensitive,
so it is not a casing problem). Note that keyed injection and `GetKeyedService` throw this too —
the registration is a factory, so even the "optional" path runs it. Use
[`pool.TryGetConnection`](connections.md#connection-lookup) when the id may legitimately not be
configured.

## The host fails at startup with options validation errors

**This is the library working as intended, and the message names every bad entry at once.** All
configuration is validated at boot: malformed AMS Net IDs (`999.1.1.1.1.1` fails rather than being
laundered to `0.1.1.1.1.1`), out-of-range ports, non-positive timeouts, malformed
[raw-channel seed entries](configuration.md#rawchannels-section), and alarm targets with no
`SymbolPath`. Fix what the message names; the [configuration reference](configuration.md) has the
rules per key. Two entries worth knowing about because the *symptom* precedes the rule:

- **`"Port": "0x851"` in a raw-channel seed is port 2129, not 851** — hex is accepted, so it binds
  fine and seeds a channel nobody reads. The canonical TC3 runtime port is decimal `851`.
- **A seed `Port` that is not a number, or a slot written as a bare value, is discarded by the
  configuration binder** before validation can see its contents — which is exactly why an entry
  with a missing port fails startup by name instead of silently seeding nothing.

## `PlcAlarmShapeException`

**The PLC's `ST_ErrorEntry` no longer matches what the alarms package binds** — a member was
removed or retyped, typically after a PLC program update. The exception names the member and the
symbol path. The snapshot is dropped whole, the outstanding set keeps its last good reading, and
the subscription recovers on the next well-formed notification; the report fires once per outage,
not once per cycle. See [PLC alarms → Fault tolerance](alarms.md#fault-tolerance). A bad *value*
in one slot (an out-of-range `ErrorType`, a nonsense timestamp) is deliberately **not** treated as
a shape mismatch and only logs a `Warning`.

## `PlcAlarmAcknowledgeException` naming an enum member

**The PLC's acknowledge method answered with a `deaReturnType` member this package does not map** —
only `SUCCESS` (→ `true`) and `NOT_FOUND` (→ `false`) are mapped; anything else raises, naming the
member, because inventing a meaning for `LOCKED` or a vendor-specific state would be a guess. The
resolution is by **name**, not number, so a PLC-side renumbering does not break it. What the named
member means is the PLC program's business — read its declaration of `deaReturnType`. A vendor
whose PLC acknowledges differently can register its own
[`IPlcAlarmDialect`](alarms.md#acknowledgement).

## A test assertion fails on a value that looks equal

```
Expected a write of 23.5 (Single) to "GVL.Setpoint" on plc1, but 2 write(s) were recorded:
  [0] 23.5 (Double)
```

**Likeliest cause: the CLR types differ.** Comparison is `Equals`-based and therefore
type-sensitive — a boxed `float` 23.5 does not equal a boxed `double` 23.5, which is why
`PlcAssertionException` prints the types. Match the type the code under test actually writes
(`23.5` is a `double`; write `23.5f` to assert a `float`). See
[Testing → Assertions](testing.md#assertions).

## A raw-channel read answers `DeviceInvalidOffset` in simulation

**Likeliest cause: the slot was never seeded.** An unseeded read answers `DeviceInvalidOffset` —
the same code real hardware gives for an unknown index — so your error handling is exercised
rather than handed fake success. [Seed the slot](raw-channels.md#simulation) with the bytes your
decoder expects. If you *did* seed it, check the seed entry actually matched: the Net ID matches
after normalisation, but the **port** must be exactly right — and remember
[`"0x851"` is 2129](#the-host-fails-at-startup-with-options-validation-errors).

## Alarms never arrive

**Likeliest cause: the handler was attached too late.** The monitor registers its subscriptions
during host startup, and ADS delivers the whole outstanding set *inside* `StartAsync` — an
`AlarmChanged` handler attached after the host starts silently misses every alarm the PLC was
already holding. Register handlers with
[`AddAlarmHandler`](alarms.md#handlers), which is resolved before any subscription exists and
cannot be too late.

Also check, in order:

- **The target is not configured for monitoring.** A target absent from `PlcAlarms:Targets` is
  simply not monitored — the package is opt-in per target
  ([configuration](configuration.md#plcalarms-section-optional-dahlketwincatadsalarms)).
- **You asked before the host started.** Before `StartAsync` nothing is subscribed, so
  `GetOutstanding()` is empty (and `AcknowledgeAsync` returns `false`).
- **The PLC was down at boot and the `SymbolPath` is wrong.** A target that was offline at startup
  has its registration retried on every reconnect — including a bad path, which would have failed
  startup had the target been reachable. Watch the log for a target that never reports
  `Monitoring alarms on …`; the retry failure is logged at `Error`. See
  [PLC alarms → Restarts, offline targets and ordering](alarms.md#restarts-offline-targets-and-ordering).

## Every alarm re-raises after a restart or deployment

**This is by design, and there is no way around it inside the package.** The outstanding set lives
in memory and starts empty; ADS delivers one notification on registration, so the first snapshot
after a restart reports every alarm the PLC is holding as newly `Raised` — a two-day-old alarm is
indistinguishable from one raised while the host was down. Forwarding `Raised` straight to a pager
pages the whole outstanding set on every deployment: deduplicate downstream on `Key` plus
`PlcTimestamp`, or suppress the first snapshot after start. See
[PLC alarms](alarms.md#restarts-offline-targets-and-ordering).

## `AcknowledgeAsync` returns `false`

`false` means **no such alarm among the outstanding set** (the PLC answered `NOT_FOUND`, or the
monitor never had the alarm). Likeliest cause: the key does not match — identity is the PLC's
`sKey` spelling, treated as opaque, so pass exactly what `alarm.Key` reported. Also possible: the
host has not started yet (nothing is subscribed, so nothing is outstanding), or the alarm already
ended. A `false` is a lookup miss; a *failure* to acknowledge raises instead — see
[`PlcAlarmAcknowledgeException`](#plcalarmacknowledgeexception-naming-an-enum-member) and
[the unknown-method entry](#an-rpc-call-fails-as-an-unknown-method).

## A simulated symbol reports `STRING` where the PLC would report `DINT`

**Likeliest cause: the value was seeded as a bare JSON scalar.** JSON configuration is
string-typed, so `"MAIN.Speed": "1500"` seeds the *string* `"1500"` — typed reads still convert,
but a metadata read reports `STRING` and a strict consumer notices. Declare the PLC type to get a
faithful stand-in: `"MAIN.Speed": { "value": 1500, "type": "DINT" }`. The type is never inferred
from the value's content. See [Simulation → Seeding initial values](simulation.md#seeding-initial-values).

## A struct read fails naming one member

**Likeliest cause: your .NET type declares a member the PLC's struct does not supply.** Binding
requires every member of the *target type* to be present in the decoded tree — a missing one fails
by name rather than silently defaulting, because a type that disagrees with the PLC is what you
want to hear about. Extra members on the PLC side are ignored. To read a subset, read the symbol
as `IReadOnlyDictionary<string, object?>` instead. See
[Reading a PLC struct into a .NET type](values.md#reading-a-plc-struct-into-a-net-type).

## A struct reads correctly in simulation and garbage on hardware

**Likeliest cause: member order.** Simulation binds by **name** (a decoded tree has no memory
layout); real hardware hands `T` to Beckhoff's marshaller, which maps PLC memory by **declaration
order**. A simulated target catches a misspelled or mistyped member and cannot catch a mis-ordered
one. Verify the .NET type's field order against the PLC declaration. See
[the note under struct reads](values.md#reading-a-plc-struct-into-a-net-type).

## Enumerating keyed connections finds nothing — or throws

**`GetKeyedServices<IAdsConnection>(KeyedService.AnyKey)` cannot enumerate targets**: on .NET 8
and 9 it throws (the container passes the `AnyKey` sentinel to the factory, and a sentinel names
no target), and on .NET 10 it returns empty. Injecting `IEnumerable<IAdsConnection>` likewise
yields nothing — the keyed registration deliberately does not leak into the unkeyed enumeration.
Use [`pool.GetAllConnections()`](connections.md#connection-lookup), the one supported way to
enumerate.

## A subscription callback never fires

Check, in order:

- **On a simulated target: the value never changed.** Simulated subscriptions fire on **changed**
  writes — the change test is `Equals`-based and type-sensitive, so writing a `double` 23.5 over a
  seeded `float` 23.5 *is* a change, and writing the same boxed value is not. `Seed` /
  `SetInitialValues` deliberately never fire; use `Write` to drive a change
  ([Testing → Seed, write, and what got recorded](testing.md#seed-write-and-what-got-recorded)).
- **The notification value was `null` and your `T` is a value type.** That notification is dropped
  with a logged Warning ([Subscriptions](subscriptions.md#untyped-subscription)).
- **A UI update that never lands is a threading problem, not a delivery problem.** Callbacks fire
  on a background thread; marshal to the UI thread yourself, or use the Rx companion with
  `.ObserveOn(...)`.

## The health check is green but a target is not being monitored

**`AddTwinCatAdsAlarmHealthCheck` reports alarm severity and nothing else** — a target still
waiting for its first connection has no alarms and looks healthy, indistinguishable from one that
is connected and genuinely quiet. Register the core
[`AddTwinCatAdsHealthCheck`](configuration.md#health-check) alongside it: connectivity comes from
the core check, severity from the alarms check, and the two together are the full picture.

## Shutdown hangs

**Likeliest cause: a blocking dispose on a UI thread, or a hosted service that hangs in
`StopAsync`.** The pool handle is `IAsyncDisposable` because stopping the router and reconnect
loops is genuinely asynchronous — a WinForms/WPF exit path that cannot `await` should
[block explicitly](connections.md#shutting-down). And note the no-host builder's private container
has **no `HostOptions.ShutdownTimeout`**: a service you registered through `ConfigureServices`
that hangs in `StopAsync` hangs `DisposeAsync` with it
([the hosted-service-runner caveats](connections.md#companion-packages)).
