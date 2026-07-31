# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.8.0] - Unreleased

### Changed

- **A dialect's configuration is validated only where that dialect is registered.** ([#25](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/issues/25))
  `PlcAlarmsOptionsValidator` encoded two `FB_ErrorHandler` rules — `AcknowledgeMethod` must be
  non-blank, and `AcknowledgeInstancePath` must be set when `SymbolPath` has no parent segment
  to trim — and applied them whichever `IPlcAlarmDialect` was registered, because validation
  could not see which one the container would resolve. A consumer whose dialect acknowledges by
  a pulsed trigger variable, a different RPC shape, or a write to a request array was failed at
  startup for an instance path it never reads, and the documented way out was to set that path
  to any non-blank string, which was then passed through unread.

  Those rules now live in a validator registered alongside the built-in dialect:
  `AddTwinCatAdsAlarms` registers the dialect and its validation together, or neither. **Register
  a custom dialect before `AddTwinCatAdsAlarms`** — that ordering was already documented, and it
  now decides validation as well as the dialect itself. A dialect with configuration rules of its
  own registers an `IValidateOptions<PlcAlarmsOptions>` beside itself; one with no rules registers
  nothing. Rules that hold for every dialect — the `PlcTargets` cross-reference, a named alarm
  array, a positive cycle time — are unchanged and still apply to everyone.

  `AcknowledgeInstancePath` and `AcknowledgeMethod` stay on `PlcAlarmTargetOptions`. They are
  public surface as of 0.7.0, and moving them off the vendor-neutral type is a breaking change
  that needs a way for dialect-specific configuration to bind; this is the half that is not
  breaking. A default installation validates exactly as it did in 0.7.0, down to the number of
  failures reported per boot.

### Fixed

- **One corrupt `PLCTimeStamp` no longer blinds the whole alarm array.** ([#26](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/issues/26))
  `PlcAlarmBinder` treated a `TIMESTRUCT` whose components do not describe a real date — a
  stopped or garbled PLC clock reporting month 13, say — as a `PlcAlarmShapeException`, the
  same class of failure as the PLC's `ST_ErrorEntry` being renamed. `PlcAlarmMonitor` responds
  to that by dropping the entire snapshot, which is correct for a broken type and wrong for one
  broken value.

  It did not recover. The alarm array is fixed-size with permanent slots, so the corrupt entry
  arrived again on the next notification and the one after that: `GetOutstanding()` froze at the
  last good reading indefinitely, every alarm raised afterwards was invisible, `AcknowledgeAsync`
  returned `false` for all of them because they were not in the outstanding set, and the health
  check reported on stale data with no sign it was stale. One bad slot hid 99 healthy alarms, in
  an alarms package, with nothing on the API surface to say so.

  An unreadable timestamp now binds as `default(DateTime)` — exactly as a zeroed, uninitialised
  `TIMESTRUCT` already did — and the entry is kept with the rest of its members intact. It is
  logged once per slot per target at `Warning` rather than on every cycle. **A missing or
  wrongly-typed member is unchanged**: that is the PLC's type no longer matching what this
  package binds, it affects every entry rather than one, and it still throws.

  If you read `PlcTimestamp` without checking it, note that `default` now means "no readable
  time" for a second reason. Treat it as unset rather than as midnight on 0001-01-01.

- **The alarm options validator no longer replaces a consumer's own.** Registration moved from
  `TryAddSingleton` to `TryAddEnumerable`. `TryAddSingleton` adds only when no
  `IValidateOptions<PlcAlarmsOptions>` descriptor exists at all, so registering any validator of
  your own before `AddTwinCatAdsAlarms` silently suppressed *every* built-in rule — blank
  `SymbolPath`, unknown `plcId`, non-positive `CycleTimeMs` — rather than adding to them.
  Validators now compose, which is both the fix and a prerequisite for the change above. If you
  registered a no-op validator to disable alarm validation, it no longer has that effect.

- **The core options validator no longer replaces a consumer's own.** ([#29](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/issues/29))
  The same defect as the entry above, in `Dahlke.TwinCAT.Ads` itself. `AddTwinCatAds` and
  `AddTwinCatAdsSimulation` registered `TwinCatAdsOptionsValidator` with `TryAddSingleton`, which
  adds only when no `IValidateOptions<TwinCatAdsOptions>` descriptor exists at all. A consumer who
  registered any validator of their own before calling `AddTwinCatAds` did not add a rule beside
  the built-in ones — they silently replaced every one of them: missing or malformed `AmsNetId`,
  duplicate Net IDs across targets, ports, timeouts, the router settings and the raw-channel seeds.
  Nothing warned. The application booted, and the first sign was a runtime failure against a target
  that validation was there to reject — an ADS connection error pointing at the network rather than
  at the configuration.

  Registration moved to `TryAddEnumerable`, which dedupes on (ServiceType, ImplementationType), so
  the config-bound and code-first registration paths still cannot double-register and repeat
  `AddTwinCatAds` calls stay idempotent. A consumer's rules and the built-in rules now both report
  in one startup failure. If you registered a no-op `IValidateOptions<TwinCatAdsOptions>` to disable
  core validation, it no longer has that effect.

## [0.7.0] - 2026-07-30

### Added

- **PLC alarm tracking — the new `Dahlke.TwinCAT.Ads.Alarms` companion package.** Point it at
  a TwinCAT alarm array and inject `IPlcAlarmMonitor` for a live set of the alarms outstanding
  right now, plus a stream of `Raised` / `Acknowledged` / `Cleared` / `Reoccurred` / `Ended`
  transitions as events or an `IObservable<AlarmTransition>`.

  **An alarm is outstanding while its fault is present OR it still awaits acknowledgement.**
  This is the ISA-18.2 "returned to normal, unacknowledged" state, and it is the whole reason
  the rule is computed rather than read off `IsActive`. A PLC alarm array is fixed-size with
  permanent slots: an alarm ends by `IsActive := FALSE`, never by leaving the array. Treating
  absence from the array as resolution — the obvious reading — detects nothing at all, and
  dropping an alarm the moment its fault clears loses every fault that self-clears before an
  operator sees it.

  **Identity is the PLC's `sKey`, never `Id`.** `Id` is the equipment identifier (BMK) and is
  shared by every alarm on one machine, so keying on it collapses simultaneous alarms into a
  single entry. `sKey` is the PLC's own composite key combining the equipment identifier and
  the error code — `Test_Err_60` for equipment `Test`, error code `60`, for example — and this
  package treats it as opaque: the exact spelling is the PLC program's business, and it is
  never parsed. `EquipmentId` is still surfaced, for grouping and filtering, which is what it
  is actually for.

  **A PLC that is unreachable at boot does not fail the host.** The facade's *first*
  subscription registration is not durable — it waits out `TimeoutMs`, throws, and retains
  nothing for a later reconnect — so letting that escape startup would take down alarm
  monitoring for every PLC that *is* up because one is down. Each target is registered
  independently instead: a failure is logged at `Error` and the target is re-attempted the next
  time its connection reports `Connected`, after which the core library's durable subscriptions
  carry it across reconnects on their own. Until then that target reports no alarms, and the
  rest of the fleet is monitored normally. On a plant where PLCs are powered down for
  maintenance, the alternative is a service that will not start. The attempts also run
  concurrently, so an unreachable target costs one connection timeout for the whole fleet
  rather than one each — startup stays prompt, exactly as the connection pool's does. Only
  unreachability is forgiven: a `SymbolPath` the PLC does not have is a fault no reconnect will
  fix, and still brings the host down — *if* that target answered at boot. If it did not, there
  is no startup left to fail and the bad path surfaces on the deferred retry instead, logged at
  `Error` and re-attempted on every reconnect.

  **Transitions for one target are published in the order they were computed.** ADS
  notifications arrive on a background thread and two snapshots for one target can overlap, so
  the diff and the publication are held under one per-target lock. Without it a consumer folding
  the stream into its own state could end on `Raised` after `Ended` and show a cleared alarm as
  live — the wrong direction for an alarm system to fail in. The price is explicit: **a handler
  that blocks delays that target's next snapshot**, so handlers must be quick. Other targets are
  unaffected; the lock is per target, and ordering is not claimed across targets.

  **`AlarmChanged` isolates its handlers; `Transitions` deliberately does not.** Each event
  handler is invoked separately, so one that throws is logged and the rest still receive the
  transition — invoking the multicast delegate directly would silently starve every handler
  registered after the first thrower. `Transitions` is an ordinary observable and keeps the
  standard Rx contract, under which throwing from `OnNext` is an observer bug: a subscriber that
  throws can skip the ones after it. Swallowing observer exceptions there would make this
  observable behave unlike every other one an Rx consumer composes with. Both paths guarantee
  the same thing at the boundary — the exception never escapes onto the notification thread, and
  the next transition is still delivered. `Transitions` is subscribe-only: what it hands back is
  an observable and nothing else, so no consumer can cast its way to the underlying sequence and
  complete, dispose, or push into the alarm stream for the whole process.

  **On `Ended`, the kind is the authority and the payload is not.** `AlarmTransition.Alarm`
  carries the last reading before the alarm ended, so its `IsActive` / `IsAcknowledged` describe
  that reading rather than the ended state — an alarm also ends by its slot being reused or
  blanked, in which case the newest thing ever seen is the alarm alive, and `Previous` is the
  very same instance so a consumer diffing the two sees no change at all. Re-deriving
  outstanding-ness from the payload therefore concludes the alarm is still live and never clears
  it. Trust `Ended`: it means gone, whatever the fields say. The payload is deliberately not
  normalised, because a synthesised reading would be one the PLC never produced.

  **Acknowledgement asks the PLC to acknowledge; it does not write the entry.**
  `AcknowledgeAsync` finds the alarm by `Key`, then calls the method named by
  `AcknowledgeMethod` (default `AcknowledgeAlarm`) on the function block that owns
  acknowledgement, passing that key. Writing `IsAcked` on the array entry — the obvious
  reading, and what this package did while it was being built — is what hardware disproved:
  the array is a projection, not state, rebuilt from the handler's own dictionary every scan,
  so the write succeeds, ADS reports success, and the PLC overwrites those bytes within one
  cycle. On the reference rack the write returned `true` and `IsAcked` was still `false` one
  through six seconds later. A mechanism that reports success while doing nothing is worse
  for an alarm system than one that fails outright, because an operator has no way to learn
  the difference — it was only found by reading the PLC source. Naming the alarm by key
  also retires the slot problem entirely: slots are permanent and reused, so the old path had
  to read a slot's `sKey` back before writing it, and a window remained between that read and
  the write. There is no slot in the call now, so there is no window. A `false` return means
  there was nothing to acknowledge, in any of three ways: the target is not monitored, the
  alarm is not in that target's outstanding set, or the PLC itself has nothing by that key.
  The first two are answered locally and never reach the PLC at all. What `false` never means
  is that the PLC refused — that raises `PlcAlarmAcknowledgeException`, so a caller can always
  tell "it is gone" from "try again".

  **Which function block, what it is called, and how its answer reads is the vendor's
  business.** `IPlcAlarmDialect` is that seam: one implementation binds the notification and
  performs the acknowledgement, and the shipped `FB_ErrorHandler` dialect is registered by
  default so nothing has to be configured for the layout this package was written against.
  `AcknowledgeInstancePath` and `AcknowledgeMethod` configure that default; a dialect for
  another vendor receives them and may ignore them.

  **The shipped dialect resolves `deaReturnType` by name, never by number.** Enum numbering
  in a PLC program moves — a member inserted in the middle renumbers everything after it —
  while names survive; the reference rack this was verified against publishes a numbering its
  own source no longer agrees with, so a number-based mapping is correct only against the one
  machine it was written for, and silently wrong everywhere else. `SUCCESS` and `NOT_FOUND`
  are therefore matched as strings against the members the PLC itself publishes. The same
  reasoning rules out reading the member by position: `GetEnumMembersAsync` promises
  declaration order, not dense zero-based values, and `SUCCESS := 100` is ordinary ST.
  The members are resolved *before* the call is issued, not after — every ordinary way that
  resolution can fail would otherwise surface as a failure for an alarm the PLC had already
  acknowledged.

  **A shape mismatch throws rather than degrading — and then recovers.** If the PLC's
  `ST_ErrorEntry` stops matching what the package binds, the binder raises
  `PlcAlarmShapeException` naming the offending member and the symbol path. Defaulting instead
  would publish a plausible-looking but wrong alarm list indefinitely, and for alarms that is
  worse than no list. The monitor logs that at `Error` and drops the whole snapshot — the
  outstanding set keeps its last good reading rather than a half-bound one — but the
  subscription stays live, so a transient malformation recovers on the next well-formed
  notification instead of requiring a restart.

  **Verified against a live PLC, not just simulation.** A real notification arrives as a CLR
  array of `TwinCAT.TypeSystem.DynamicValue`, with each element's members read through
  `IStructValue` rather than a dictionary, and `E_ErrorType` decodes as a plain integral
  matching the documented `None=0, Info=1, Warning=2, Error=3` numbering — none of which the
  simulated store, built from seeded primitives, could confirm on its own. The alarms observed
  on that run were themselves in the returned-to-normal-unacknowledged state, `IsActive=false`
  with `NeedsAck=true`, the exact case the outstanding rule exists to keep visible rather than
  dropping the moment a fault clears.

  **Notification cost is one payload decode, no round-trips.** The monitor subscribes through
  the untyped `SubscribeAsync`, which serves the whole array from the notification payload.
  The metadata overload would instead build a neutral tree with one ADS read per member —
  for an array of N entries with M members, N×M round-trips per notification.

  **Timestamps state a clock or state none — they never guess.** The PLC's `TIMESTRUCT`
  carries no time zone, so nothing in the payload can say whether it is UTC or the machine's
  local time. `PlcAlarmTargetOptions.PlcClock` (a `PlcClockKind`: `Unspecified`, `Utc` or
  `Local`) is how a caller declares it per target, and it is what sets the `DateTimeKind` on
  every `PlcAlarm.PlcTimestamp`. The default is `Unspecified`, which makes no claim: stamping
  a wrong `Kind` is worse than stamping none, because a consumer calling `ToUniversalTime()`
  then silently shifts every alarm in the plant by the host's offset, and a shifted timestamp
  looks exactly like a real one. Alarms are also ordered by this value — `Reoccurred` fires
  when an already-active alarm's timestamp advances — so it is load-bearing, not decorative.

  **`PlcAlarm.SlotIndex` reports where the alarm sat, and addresses nothing.** The array
  index an alarm was bound from is surfaced for diagnostics and display, because an operator
  reading a PLC's array alongside this package's output needs to line the two up. It is
  explicitly not an identity: slots are permanent and reused, so an index identifies a
  position rather than an alarm. Nothing in this package addresses an alarm by it —
  acknowledgement goes by `Key`, which is what removes the read-then-write window a
  slot-addressed design would need.

  **`IAlarmTextCatalog` is a public extension point, not just the JSON file.** Register your
  own implementation before `AddTwinCatAdsAlarms` to resolve alarm text from a database, a
  resource assembly, a translation service or anything else; the built-in JSON catalog is
  registered only when none is present, so overriding it takes no opt-out flag. Its one
  method must be safe to call concurrently — text is resolved on the ADS notification thread
  while binding each snapshot.

  Ships with a JSON alarm text catalog (`sKey` → text, with per-key culture fallback) and
  startup validation that reports every misconfiguration at once. A relative `TextCatalog`
  resolves against the host's content root rather than the process working directory — the two
  coincide under `dotnet run` and almost never do for a published or service-hosted app, so
  anchoring to the working directory would turn the most natural configuration into a
  `FileNotFoundException` that only appears on deployment. An absolute path is used as written.
  `AddTwinCatAdsAlarmHealthCheck()` reports from the worst outstanding severity — and **only**
  that. Healthy has to be earned: it needs a severity that is both a named `AlarmSeverity`
  member and below both thresholds. `E_ErrorType` is signed and an unrecognised value is
  preserved rather than dropped, so an alarm can arrive carrying a severity nothing can rank —
  `(AlarmSeverity)(-1)` sorts below `None` and clears no threshold. That reports Degraded, not
  Healthy: an outstanding alarm this package cannot interpret is a reason to look, though not
  by itself proof of a fault. It is not a liveness check either: a target still waiting for its
  first connection has no alarms and so reports healthy, indistinguishable from one that is
  connected and quiet.
  Register the core's `AddTwinCatAdsHealthCheck()` alongside it, which is what answers whether
  a target can be seen at all.

- **PLC method calls — `IAdsConnection.InvokeRpcMethodAsync`.** Calls a method on a function
  block or program instance by path and name, with input arguments in declaration order, and
  returns the method's return value alongside its output parameters as an `AdsRpcResult`.
  Until now the library could read and write symbols but could not ask the PLC to *do*
  anything, which for the alarm package meant the only acknowledgement it could express was a
  write to a projection that ignores writes. The PLC method must carry
  `{attribute 'TcRpcEnable'}` — without it TwinCAT does not expose it over ADS at all and the
  call fails as an unknown method, whatever the path says.

  **`AdsRpcResult` carries Beckhoff's own value shapes, not this library's neutral tree.** A
  scalar arrives as a boxed primitive, which is what makes the common case look familiar, but a
  struct or an array arrives as a `DynamicValue`-family object implementing `IStructValue` or
  `IArrayValue` — not an `IReadOnlyDictionary<string, object?>` and not an `object?[]`. Casting
  it to either throws. Decoding a returned container would require type metadata for the
  method's signature, which this library deliberately does not fetch; when the neutral tree is
  what you want, read the symbol with `ReadValueWithMetadataAsync`, which does decode.

- **PLC enum metadata — `IAdsConnection.GetEnumMembersAsync`.** Resolves an enumeration's
  members — name and numeric value, in declaration order — from the running program's own type
  metadata, so a returned code can be read by the name the PLC gives it. Numbering is not
  stable across a project's life and names are: a member inserted in the middle renumbers
  everything after it, and a machine can be running a numbering its own source no longer
  agrees with. Code that maps a returned integer by number is therefore correct only against
  the machine it was written for and silently reports a different member elsewhere, with no
  error anywhere to catch it. The result is cached for the life of the connection, since PLC
  type metadata is fixed for a running program — which also means a download that changes an
  enum is not seen until the connection is re-established. Resolving a type for the first time
  makes the connection upload the running program's whole type system, which is a synchronous,
  uncancellable Beckhoff operation; it is performed off the calling thread so that the
  cancellation token and the per-target `TimeoutMs` genuinely bound the wait, as they do for
  every other operation on the connection. Later calls for the same type are served from the
  cache and wait for nothing.

  Both surfaces exist on simulated connections too, seeded code-first by
  `SimulatedAdsConnection.SetRpcHandler` and `SetEnumMembers`. Neither has a fallback: an
  unseeded call **throws** rather than returning something plausible. A simulated
  acknowledgement that appears to succeed while doing nothing is indistinguishable from one
  that worked, and that is precisely the defect this release removes — simulation is not
  allowed to stage it.

- **`Dahlke.TwinCAT.Ads.Examples.ErrorHandler`** — a console example that walks a scripted
  alarm lifecycle in simulation: two alarms on one machine, one that clears before it is
  acknowledged, and an acknowledgement that ends it by calling a seeded `AcknowledgeAlarm`
  method on the simulated PLC. The driver derives each entry's `IsAcked` from what that
  method recorded rather than from a literal, so `[ACKNOWLEDGED]` in the output is evidence
  the call reached the dialect and not a staged write.

## [0.6.0] - 2026-07-30

### Added

- **Raw ADS channels — a low-level index-group/index-offset surface.** Until now the library
  exposed nothing below the symbol layer, so reaching an EtherCAT master, a CoE object
  dictionary, or the TwinCAT system service meant dropping to `new AdsClient()` and rebuilding
  connection lifetime, timeout and retry by hand.

  Inject `IAdsRawChannelFactory` — registered by `AddTwinCatAds` alongside the pool — and call
  `Get(amsNetId, port)` for a cached, permanent `IAdsRawChannel` carrying `ReadAsync`,
  `WriteAsync`, `ReadWriteAsync`, `ReadStateAsync` and `SubscribeAsync`. Targets are named at
  the call site rather than declared in configuration, which is what discovery-driven use cases
  need: an EtherCAT master's Net ID is not known until you have asked for it.

  `ReadWriteAsync` is present because the ADS *ReadWrite* service is not optional: the file
  protocol's `FILE_OPEN` sends the path as write data and returns the handle as read data, so a
  read/write-only surface cannot express it at all.

  **Channels are never disposed by consumers.** `Get` is total — it never blocks, and the only
  input that throws is a `null` Net ID, which is a caller programming error rather than a target
  that happens not to exist. Reachability is therefore discovered by operating, not by
  obtaining. Channel identity is permanent; idle eviction drops the underlying transport, not
  the facade, and the next operation reconnects. This deliberately avoids a refcounted lease,
  whose correctness would depend on every caller disposing exactly once — the ownership
  ambiguity that produced the three-instalment teardown race in #9/#13/#15.

  **The Net ID is normalised, so one device is one channel.** `"1.2.3.4.5.6"`,
  `"01.2.3.4.5.6"` and `" 1.2.3.4.5.6"` all return the same channel and, in simulation, share
  one seedable store. An octet outside 0–255 is **zeroed, not rejected** — `Get("999.1.1.1.1.1",
  851)` returns the channel for `0.1.1.1.1.1` — because that is how the ADS stack itself
  resolves the address when the transport connects, so the channel really does reach the device
  the key names. A warning is logged once per distinct spelling. The same Net ID used as an
  `AdsRawChannelOptions.Seed` entry **fails the host at startup** instead: a seed entry is a
  declaration whose typo has no correct reading, whereas a lookup's only correct answer is what
  the wire will do.

  **Timeouts bound each ATTEMPT, not the retry sequence.** With the defaults a call can
  therefore take up to 10 seconds before throwing `TimeoutException`: `RetryCount` of 1 permits
  two attempts of 5000 ms. The worst case for the attempts themselves is
  `TimeoutMs × (RetryCount + 1)`. This matches how consumers already retry by hand, and the
  reason is mechanical — a retry re-creates the transport before reissuing, because a fresh
  client is what clears the stall. Reusing the stalled one would look like a retry and behave
  like a second timeout. A call that also has to *build* the transport for a channel with live
  subscriptions additionally waits for that rebuild's restore pass, which re-registers those
  subscriptions one at a time under their own separate bounds; that wait precedes the call's own
  attempt bound rather than being contained by it.

  **An ADS error code is an answer, not a failure.** It is never retried and never tears the
  channel down. That distinction matters on EtherCAT, where `PortNotConnected`,
  `TargetPortNotFound` and `DeviceTimeOut` are the ordinary replies from a slave with no mailbox
  — treating them as transport death would rebuild the connection on every probe of a
  mailbox-less slave.

  **Raw notification handlers take `ReadOnlySpan<byte>`**, via the named `RawNotificationHandler`
  delegate rather than `Action<ReadOnlyMemory<byte>>`. The buffer belongs to the transport and is
  reused once the handler returns; a `ReadOnlyMemory` could be captured and read later, yielding
  silently wrong bytes with no exception and no stack trace. A span cannot be captured or stored,
  so that mistake is a compile error, and a handler that needs the data writes a visible
  `data.ToArray()`. The cost is that handlers cannot be `async`.

  Subscriptions are durable: a transport drop re-registers every live one against the fresh
  transport — exactly once — while the caller's handle stays valid, and a live subscription pins
  its channel against idle eviction. **Registration is bounded by `TimeoutMs` but is never
  retried**, because retrying it would mean dropping and rebuilding the transport, re-registering
  every *other* subscription on the channel as a side effect of one subscriber's retry.

  **After the host has shut down, `Get` still returns a channel but operating on it fails fast**
  with `AdsConnectionUnavailableException` rather than opening a transport nothing would ever
  release. This matters for a consumer hosted service that stops after the factory does, and
  mirrors the rule `IAdsConnection` already applies to a stopped pool.

- **Simulation for raw channels.** `AdsRawChannelOptions.Mode` selects a seedable byte store,
  populated up front from `AdsRawChannelOptions.Seed` or at runtime via
  `IAdsRawChannelFactory.TryGetSimulated`. The store carries **no protocol knowledge** — it does
  not know what an index group means or how a file request is framed; a consumer seeds the exact
  bytes its own decoder should read back. An unseeded read answers `DeviceInvalidOffset`,
  deliberately the code real hardware gives for a bad offset, so error-classification paths run
  in simulation rather than only against a device.

  Seed entries are validated at startup in **both** modes, so a malformed entry left
  behind after a switch to `Real` still fails the host rather than sitting silently broken.

  **`RawChannels` binds from `IConfiguration`**, alongside `PlcTargets` and `AmsRouter`, and is
  equally settable through an `AddTwinCatAds(…)` options delegate. `Seed` is an **array of
  objects** — `{ AmsNetId, Port, Slots: [{ IndexGroup, IndexOffset, Bytes }] }` — rather than a
  dictionary keyed on `amsNetId:port`: `:` is the configuration hierarchy separator, so such a
  key flattens into nested sections and loses the port and both slot indices. `IndexGroup` and
  `IndexOffset` are strings so that `0x`-prefixed hex keeps working; both decimal and hex are
  accepted. A seed entry's Net ID is matched against the channel after normalisation, so
  `01.2.3.4.5.6` still seeds `1.2.3.4.5.6`.

  A seed Net ID is validated more strictly than `IAdsRawChannelFactory.Get` accepts: `Get`
  resolves an out-of-range octet the way the ADS stack does, whereas a seed entry with the same
  typo fails the host at startup, because a declaration's typo has no correct reading. A seed
  `Port` takes decimal or `0x`-prefixed hex, so the conventional `0xFFFF` for an EtherCAT master
  works in configuration as well as at a call site. Note the corollary: `"0x851"` is **2129**, not
  851 — the canonical TC3 runtime port is decimal `851` and must be written that way.

  **A seed entry or slot the configuration binder cannot bind now fails the host instead of
  disappearing.** `ConfigurationBinder` reports a bad *scalar* by throwing with the offending
  path, but swallows the same failure inside a *collection element* and drops the element. So
  `"Port": "typo"` bound to a seed list silently missing that target, and a slot written as a bare
  value rather than an object — `"Slots": [ "0x11", "0x12" ]` — dropped every slot, leaving the
  target reachable but unseeded so every read answered `DeviceInvalidOffset`. Both are the
  identical failure mode the array shape was adopted to eliminate, reached by different routes;
  entry and slot counts are now both checked.

- **`AmsRouter:Routes` — remote routes for the embedded router.** Without this, a host on a
  machine with no TwinCAT installation could not reach a remote PLC **at all**: the embedded
  router started with an empty route table and `AmsRouterOptions` exposed only `NetId`, so
  nothing could tell it where the device was. Verified against a live rack — the identical code
  path fails with `TargetMachineNotFound` without a route and succeeds with one, same machine and
  same Net ID.

  It was invisible on Windows, where the OS router already holds the routes, which is also why
  the hardware suite had never been runnable off Windows.

  ```json
  "AmsRouter": {
    "NetId": "192.168.1.220.1.1",
    "Routes": [
      { "Name": "rack", "NetId": "5.138.44.199.1.1", "Address": "192.168.1.223" }
    ]
  }
  ```

  **A new option rather than a Beckhoff configuration key, because no such key exists.** Four
  candidate spellings were measured against the `AmsTcpIpRouter(IConfiguration, …)` overload —
  `StaticRoutes:0:*`, `RemoteConnections:R:*`, `Router:StaticRoutes:0:*` and
  `Ams:StaticRoutes:0:*` — and all yielded zero routes. Beckhoff's only other source is a TwinCAT
  `StaticRoutes.xml` on disk, absent on exactly the machines that need the embedded router.

  `Address` takes an IP address or a host name; the router resolves either. Entries are added
  **after** the router has started — the ordering that was verified on hardware — and **before**
  the readiness signal releases the connection pool, so a pool connection never races a route
  that is not in the table yet. Each is logged at `Information` with its name, Net ID and
  address; a route the router rejects is logged at `Warning` naming it rather than thrown, since
  throwing would tear down a working router and make one unreachable device cost every reachable
  one. Routes configured while `AmsRouter:NetId` is unset are warned about rather than ignored in
  silence.

  A route's `NetId` is validated with the **strict** six-octet 0–255 check shared with
  raw-channel seed entries, deliberately not `AmsNetId.TryParse`: that method *launders* an
  out-of-range octet, returning `true` for `999.1.1.1.1.1` and yielding `0.1.1.1.1.1`, so
  delegating would let a typo'd route silently address a different device. `Name` and `Address`
  must be non-empty, and duplicate names fail the host because the router keys routes by name.
  Routes are validated whether or not the embedded router is enabled, so a typo left behind after
  a switch to the system router still fails rather than waiting to be rediscovered. A route
  element the configuration binder discards — `"Routes": [ "rack" ]`, a bare value where an
  object belongs — fails the host too, the same protection seed entries and slots already have.

- **`IAdsConnectionPool.GetTargetStates()`** — a public per-target status snapshot
  (`PlcTargetStatus { PlcId, Mode, State }`, ordered by id), so dashboards and status
  endpoints read the same truth the health check reports without scraping `/health`.

### Changed

- **BREAKING: `PlcTargets:{id}:AmsNetId` and `AmsRouter:NetId` now reject an out-of-range octet
  instead of laundering it.** Both keys were validated with `AmsNetId.TryParse`, which returns
  `true` for `999.1.1.1.1.1` and yields `0.1.1.1.1.1` — the octet is *zeroed*, not reduced modulo
  256, so `256`, `300`, `512` and `999` all collapsed to one address. A host with a typo'd target
  therefore booted, could report healthy, and talked to a device nobody wrote down; a typo'd
  `AmsRouter:NetId` started the embedded router under an address every route it served could only
  reach by accident. Both now fail at startup, naming the key and the offending value.

  **A configured Net ID is a declaration, so it gets one rule.** Routes and raw-channel seed
  entries were already held to the strict six-octet 0–255 check; these two keys were not, and the
  validator's own remarks called the gap a deferred behaviour change rather than a principle. It
  is deferred no longer: the rule, its explanation and its one message template now live in
  `AmsNetIdRule`, and all four configured Net IDs go through it. The project is pre-1.0.0 and
  0.6.0 is unreleased, so the tightening ships in the same release as the strict route and seed
  checks rather than contradicting them one release later.

  **The runtime lookup path is deliberately unchanged.** `IAdsRawChannelFactory.Get` stays
  documented-total: it canonicalises the Net ID, warns once per distinct laundered spelling, and
  never throws. That asymmetry is load-bearing rather than an oversight — the transport launders
  identically at `Connect()`, so collapsing the spellings is what keeps the channel key agreeing
  with the address actually dialled. Strict for declarations, lenient for lookups, and the two
  are now named that way in one place instead of spelled four ways across six.

  Empty and null keep their existing meanings at both keys: a `Real` target still reports
  `AmsNetId` as required and missing, and an absent `AmsRouter:NetId` still means "use the system
  router".

- **The embedded AMS router now starts when raw channels are real, even if every configured PLC
  target is simulated.** Raw channels have no symbol layer to fall back on and cannot route
  without it, so the previous `_hasRealTargets` gate would have left every `Get` failing at
  connect in such a host. `AddTwinCatAdsSimulation` correspondingly forces `RawChannels.Mode` to
  `Simulated`, so the helper whose entire promise is "no hardware needed" cannot quietly start a
  router.

- **`AdsClient.Timeout` is now set explicitly on both client construction sites, as a backstop
  that is deliberately never the effective bound.** The library's own `CancellationTokenSource`
  remains the authority, because `AdsClient.Timeout` is a per-*client* property on a client that
  serves every concurrent caller, so it cannot express a per-call bound — it can only cap one.
  Wiring it to the configured operation timeout was tried and reverted: on a channel configured
  at 750 ms, a caller passing an explicit 2 s override would have been cut off at 750 ms and, worse,
  received `AdsErrorException`/`ClientSyncTimeOut` instead of `TimeoutException` — a timeout
  wearing an answer's clothes.

  **The two backstops deliberately differ in shape.** The symbol layer derives its value as
  `2 × max(TimeoutMs, SymbolBrowseTimeoutMs)` (computed in `long` and clamped to `int.MaxValue`),
  which is possible because that layer has no per-call timeout override — both bounds are known
  at construction. The raw path instead uses a flat **one hour**, because its per-call `TimeSpan`
  overload is both unvalidated and unknown at construction time, so no construction-time formula
  could bound it. One hour rather than `int.MaxValue` is also deliberate: it is not yet verified
  whether cancelling the linked token aborts the underlying ADS transaction or merely abandons
  the await. If it only abandons, that constant is how long an abandoned request stays
  outstanding — `int.MaxValue` ms is ~24.8 days, an hour self-heals. Settling that question needs
  hardware and has not been done.

- **`Beckhoff` obsolete-overload suppressions are confined to one file.** The index-group
  overloads are `[Obsolete]` in 7.x with no `AdsClient`-compatible replacement;
  `BeckhoffManagedRawConnection` is now the single permitted `#pragma warning disable CS0618`
  site, so consumers no longer carry it.

- **The internal connection seam no longer carries the consumer state surface.**
  `IManagedConnection` is decoupled from `IAdsConnection`: connection-state ownership lives
  with the pool and is surfaced through the facade, so the internal `AdsConnection` (and the
  test doubles) no longer implement a `State` property nothing read and a
  `ConnectionStateChanged` event nothing could raise. `SimulatedAdsConnection` keeps both —
  handing a sim directly to code expecting an `IAdsConnection` is a supported testing pattern,
  and for a connection that is permanently connected they are honest — and now implements
  `IAdsConnection` directly. No public API was removed.

- `TwinCatAdsHealthCheck` consumes `IAdsConnectionPool` instead of the concrete pool, and its
  documentation no longer describes a router-release distinction the code never performed.

- **One durable-subscription module instead of two implementations.** The invariants that make
  a subscription survive its connection — publish-before-first-registration, reserve then
  commit-or-hand-back (exactly-once per target), restore-on-swap with per-record isolation and
  retain-on-failure, the rule that a restore never runs on the triggering caller's token, and
  the disposal/delivery guarantee — were implemented separately in the symbol facade and the
  raw channel, with the raw copy citing the facade copy in prose and the two restores stating
  the token rule differently. They now live once, in an internal
  `DurableSubscriptionRegistry`, unit-tested directly; the facade and the raw channel are its
  two adapters, each contributing only its genuinely local policy (dispose-vs-
  `RemoveNotification` discard, the facade's current-pointer commit guard, the raw channel's
  shutdown-linked restore bound). No public API or behaviour change — the existing facade,
  contract, and raw-channel suites pass unchanged.

- **One in-memory PLC instead of four.** The store, the fire rule, the subscriber mechanics
  and the symbol-tree derivation behind every simulated/in-memory data plane — the symbol
  simulation, the raw simulation, and the test project's two in-memory doubles — were four
  separate implementations of the same invariants. They now compose three shared internal
  modules (`InMemoryPlcStore` — slots + the one fire rule; `SubscriberRegistry` —
  snapshot-then-fire delivery with per-callback isolation; `SimulatedSymbolTree` — the
  dotted-path tree walk), each pinned by its own unit tests. Store lifetime remains each
  owner's stated choice: the symbol sim's store dies with its connection, the raw store
  stays factory-owned so seeds survive idle eviction.

  Two behaviours changed with the consolidation, both deliberate:
  - **Raw simulated notifications now fire on CHANGE of byte content, not on every write** —
    matching the real transport, which registers `AdsTransMode.OnChange`, and matching what
    the raw sim's own documentation already claimed. (The raw surface is new in this release,
    so no shipped behaviour changes.) Pinned by a new contract fact on both raw harnesses.
  - **Symbol subscription paths are now case-insensitive like the store they watch.** The
    subscriber dictionaries compared case-sensitively while the stores compared
    case-insensitively, so a subscriber registered under one casing silently never fired for
    a writer using another.

- **One owned-loop teardown primitive instead of three hand-rolled copies.** The
  CTS-ownership discipline — teardown paths cancel-only (never dispose, never throw, any
  number of times); the owning loop alone retires its signal, in its own `finally`, after it
  has exited; a signal nobody owns may live undisposed — was implemented three times, in the
  pool's reconnect loops, the raw factory's idle sweeper, and the raw channel's shutdown
  signal, each defended by a long comment citing the same root-cause hang (#15). It now lives
  once in an internal `OwnedLoopCancellation`, unit-tested directly (including the
  cancel-vs-retire races the three copies each argued about in prose); the three sites become
  its call sites. The raw factory's stop-before-sweep ordering, previously repeated in
  `StopAsync` and `Dispose` and asserted only in a comment, is now encoded once in a shared
  `BeginTeardown`. No behaviour change.

- **Production-dead internal surface pruned** (no public API change). `AdsVersionFormatter`
  (25 lines for one interpolated string, one caller) is inlined; `AdsConnectionFacade.Clear()`
  (zero production callers, a doc claiming a pool-stop role `MarkStopped` actually fills) and
  `AdsRawChannel.ConnectAttempts` (written, never read) are deleted. The facade's
  `CurrentForTesting` is renamed `Current` with an honest doc — it backs the public
  `TryGetSimulatedConnection`, so its name and "exposed for tests" doc were lying about a
  production dependency. `Iec61131Converter`'s strict tier is deliberately KEPT public, with
  the rationale now recorded in its doc: the lenient `Beckhoff` tier is built by delegation on
  the strict core, so it is load-bearing — and its public door exists for consumers, not for
  the library.

### Fixed

- **`Connected` now means "can carry ADS traffic", proven before it is published.** (#12)
  Beckhoff's `AdsClient.Connect` is purely local — it associates an AMS address and succeeds
  even when the peer is unreachable — and the pool used to declare `Connected` on its strength
  alone. The first real round trip (subscription re-registration, or the health check) was what
  discovered the dead link: a physical cable-pull test measured three connect attempts and
  ~30 s of noisy recovery, and a misconfigured `AmsNetId` reported `IsConnected == true` while
  every operation failed with `TargetMachineNotFound`.

  The pool now proves the link with one `ReadState` round trip between `Connect()` and the
  publish point. A failed probe is a failed connect attempt — torn down unpublished, backed
  off, retried — so the `Connected` transition, the facade's routing, `IsConnected`, and
  durable-subscription re-registration all wait for a link that has answered. One outage now
  produces exactly one `Connected` and one re-registration pass.

- **Configured symbol-layer timeouts were unreachable: Beckhoff's invisible 5000 ms default was
  the real bound.** `AdsClient.Timeout` was never assigned anywhere in `src/`, so the Beckhoff
  client's own 5000 ms default capped every symbol operation regardless of configuration. Two
  consequences, both shipped since 0.5.3:

  - `PlcTargetOptions.TimeoutMs` above 5000 could not be reached.
  - `PlcTargetOptions.SymbolBrowseTimeoutMs` **defaults to 30000** and was therefore guaranteed
    unreachable out of the box. `IAdsConnection`'s documented behaviour — racing the browse
    against `SymbolBrowseTimeoutMs` on the thread pool — bounds the *caller's wait* correctly,
    but nothing could extend the client's own cap underneath it. A browse the author clearly
    expected to be allowed 30 s (a large PLC's symbol upload plausibly needs it) aborted at 5 s.

  In both cases the failure also arrived in the wrong shape: `AdsErrorException` with
  `AdsErrorCode.ClientSyncTimeOut`, which this library's own contract defines as a device
  *answer*, rather than the documented `TimeoutException`.

  **Migration.** Two behaviour changes for existing consumers:

  1. **`TimeoutException` now replaces `AdsErrorException`/`ClientSyncTimeOut` for timeouts over
     5 s.** Code catching `AdsErrorException` to detect a slow timeout on a target configured
     above 5000 ms must switch to `TimeoutException`.
  2. **A symbol browse that previously died at 5 s can now legitimately run to 30 s** (or to
     whatever `SymbolBrowseTimeoutMs` says). Anyone who was relying on the fast 5 s failure as a
     liveness signal now waits longer. Lower `SymbolBrowseTimeoutMs` explicitly if that matters.

  No existing test relied on the 5 s cap: the symbol-browsing tests inject a fake loader and
  never call `Connect()`, so nothing in the suite could ever have been sensitive to
  `AdsClient.Timeout`.

- **`PlcTargetOptions.SymbolBrowseTimeoutMs` is now validated at startup.** It previously had no
  validation at all, while `TimeoutMs` did. That was inert as long as the 5 s cap always won, but
  the value flows into `Task.Delay(SymbolBrowseTimeoutMs, …)`, which throws
  `ArgumentOutOfRangeException` for any negative other than `-1` — so lifting the cap turned
  `SymbolBrowseTimeoutMs: -5` from harmless into a crash on the first browse. It must now be
  greater than zero, checked at startup with the other target options and reported alongside
  them. No upper bound is imposed on either timeout: picking a ceiling would be an unasked-for
  policy decision, and the backstop arithmetic already clamps large values safely.

- **Two symbol-browsing tests could fail under CPU pressure** (test-only, and pre-existing on
  `main` rather than introduced here). `SearchSymbolsAsync_AbandonsSlowBrowse_…` and
  `SearchSymbolsAsync_CallerCancels_…` both raced a fake that blocks a thread-pool thread for a
  full second while xUnit runs collections at core count — two, under the constrained-container
  recipe this project uses to reproduce teardown races. One asserted a hard wall-clock bound,
  which is unsound under load; the other was already ordering-based, but ordering alone did not
  save it, because a starved continuation lets the browse win `Task.WhenAny` outright and
  `ThrowsAsync` then fails with "no exception" before any ordering assertion is reached. Both now
  hold the browse on an explicit gate, the way a third test in the same file already did.

  **The justification is mechanistic, not statistical.** The gate makes the race window
  structurally unreachable — the enumeration cannot complete until the test releases it — so
  neither the exception type nor the ordering can be decided by scheduler luck. The measured
  evidence alone would not support a stronger claim: 0 failures in 15 whole-suite container runs
  against a historical ~16% (4 of 25) does not statistically establish improvement, since the
  one-sided 95% upper bound on 0/15 is ≈18%.

## [0.5.3] - 2026-07-29

There is no 0.5.2 release. The two fixes below that were staged under that number never
shipped on their own — they are first available here, so 0.5.3 follows 0.5.1 on NuGet.

### Fixed

- **Pool shutdown could hang forever when `StopAsync` and `Dispose` ran concurrently.** ([#14](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/issues/14))

  The third and final instalment of the #9 teardown race — and the one that explains the
  other two. `CancellationTokenSource.Dispose()` is not safe to call while another thread is
  inside `Cancel()` on the same source. Only one caller executes the registered callbacks; a
  second `Cancel()` sees the source already cancelling and returns **immediately, without
  waiting for those callbacks to run**. If that second caller then disposes, it frees the
  registration list while the winner is still walking it, and a pending registration is
  dropped without ever being invoked.

  Both teardown paths did exactly that: `StopAsync` cancelled the per-target reconnect
  sources while `Dispose` cancelled *and disposed* them. The connection loop parks on

  ```csharp
  await Task.Delay(HealthCheckInterval, _timeProvider, cts.Token);
  ```

  whose completion depends entirely on that token registration. Lose it and the delay never
  completes, the loop task never finishes, and `StopAsync` waits on it forever.

  This is the same root cause as the `ObjectDisposedException` fixed in 0.5.1 and 0.5.2 —
  one race, two symptoms. Guarding the exception made the loud symptom quiet without
  removing the race, which is why the hang survived both earlier fixes.

  Ownership is now unambiguous: **the loop that creates a reconnect source is the only thing
  that disposes it**, in a `finally` after it has exited and can no longer hold a
  registration. Every teardown path (`StopAsync`, `Dispose`, `ForceReconnect`) cancels only.
  The task tracked in `_loopTasks` is the loop plus its disposal, so a caller awaiting it
  knows the source is retired by the time it returns.

  The hang needed Linux, `--cpus=2`, and the whole test suite in one process before it would
  reproduce — isolating any one of those gives a false all-clear. Under that harness it hung
  ~15–20% of runs before this change and 0 of 42 after. The recipe is recorded on
  `ConcurrentStopAndDispose_DoNotThrow` so it does not have to be rediscovered.

- **`AdsConnectionPool.StopAsync` could still throw `ObjectDisposedException` during concurrent shutdown.** ([#9](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/issues/9))

  A follow-up to the 0.5.1 teardown fix, which missed one call site. `StopAsync` cancels the
  per-target reconnect sources before draining them:

  ```csharp
  foreach (var (_, cts) in _reconnectCts)
      cts.Cancel();                       // unguarded
  ```

  Unlike `DrainReconnectCts`, this loop deliberately does not take ownership — the loops still
  have to observe cancellation and finish before their sources can be disposed. That leaves a
  window a concurrent `Dispose` can win: it drains and disposes a source while this enumeration
  still holds a reference to it, and the `Cancel` throws with the original issue's signature:

  ```
  System.ObjectDisposedException : The CancellationTokenSource has been disposed.
     at System.Threading.CancellationTokenSource.Cancel()
     at Dahlke.TwinCAT.Ads.AdsConnectionPool.StopAsync(CancellationToken)
  ```

  The `Cancel` is now guarded exactly as the stopping source above it already was. A source the
  other path disposed is one it already cancelled, so skipping it loses nothing.

  Reproduction is core-count sensitive, which is why 0.5.1 shipped with it and why the existing
  `ConcurrentStopAndDispose_DoNotThrow` test passes on a typical dev box: under
  `DOTNET_PROCESSOR_COUNT=2` it failed 14 times in 25 runs before this change and 0 in 65 after.

- **Config-seeded `InitialValues` lost their type: simulated reads returned `STRING` where a real PLC returns `DINT`/`BOOL`/`LREAL`.** ([#10](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/issues/10))

  `IConfiguration` is string-typed all the way down, so a JSON `1500`, `21.5` and `true` all
  reach the options binder as the strings `"1500"`, `"21.5"` and `"true"`. Bound into
  `Dictionary<string, object?>` they stayed strings, and `SimulatedAdsConnection.InferPlcType`
  — which reads the CLR type and nothing else — reported `STRING` for every one of them:

  | Symbol | Real PLC | Config-seeded simulation (before) |
  |---|---|---|
  | `MAIN.Speed` | `1500` / `DINT` | `"1500"` / `STRING` |
  | `MAIN.Setpoint` | `21.5` / `LREAL` | `"21.5"` / `STRING` |
  | `MAIN.Running` | `true` / `BOOL` | `"True"` / `STRING` |

  That made a file-configured simulation an unfaithful stand-in for hardware, which is the main
  reason to use one. Only the config path was affected — `SetInitialValues` called
  programmatically has always preserved CLR types, which is why a test suite that seeds in code
  could not catch it.

  An `InitialValues` entry may now declare its PLC type, and is converted to that type at bind
  time so everything downstream (untyped reads, batch reads, notification metadata, symbol
  browsing) reports it correctly:

  ```jsonc
  "InitialValues": {
    "MAIN.Speed":    { "value": 1500, "type": "DINT"  },
    "MAIN.Setpoint": { "value": 21.5, "type": "LREAL" },
    "MAIN.Running":  { "value": true, "type": "BOOL"  },
    "MAIN.Cycle":    { "type": "TIME" },        // no value → the type's default
    "MAIN.Station":  "Demo Station"             // bare scalar → seeded as STRING, as before
  }
  ```

  `type` accepts any IEC 61131-3 elementary type name, matched case-insensitively with Beckhoff
  aliases resolved (the same lenient tier the library uses for TwinCAT-reported type names).
  The type is deliberately **not** inferred from the value's content: a genuine `STRING` symbol
  whose value happens to look numeric must not silently become a `DINT`.

  Bare scalar entries keep their existing behaviour and are still seeded as strings, so nothing
  that worked before changes. Code-first seeding is untouched.

  The stock configuration binder cannot express this — a nested `{ "value": …, "type": … }`
  object bound into a `Dictionary<string, object?>` yields a bare `System.Object` with the
  children dropped — so the section is re-read directly after `Bind`. Reshaping `InitialValues`
  into a dedicated entry type would bind natively but would be a source-breaking change for
  code-first callers, so it was not done. Only keys present in configuration are touched;
  code-first values layered on afterwards survive.

  A malformed entry — unknown type name, a value that will not convert, a `value` with no
  `type`, an unrecognised key, or a non-scalar `value` — is now an options-validation failure at
  startup rather than a silently mistyped symbol. Failures are aggregated with the rest of the
  validator's, so every bad entry is reported in one go.

## [0.5.1] - 2026-07-29

### Fixed

- **`AdsConnectionPool` threw `ObjectDisposedException` during host shutdown.** ([#9](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/issues/9))

  A pool registered through `AddDahlkeTwinCatAds` is both an `IHostedService` and a disposable
  singleton, so shutdown runs two independent paths against the same fields with nothing
  serialising them: the host calls `StopAsync`, and the DI container disposes the singleton.
  `StopAsync` could then reach `_stoppingCts.Cancel()` on a source `Dispose` had already disposed:

  ```
  System.ObjectDisposedException : The CancellationTokenSource has been disposed.
     at System.Threading.CancellationTokenSource.Cancel()
     at Dahlke.TwinCAT.Ads.AdsConnectionPool.StopAsync(CancellationToken)
  ```

  The `?.` guards read as thread-safety but are not — the null check and the use are separate
  reads of a mutable field, so the other path can dispose in between. Teardown now transfers
  ownership atomically (`Interlocked.Exchange` for the stopping source, `TryRemove` for the
  per-target reconnect sources and live connections), so exactly one caller cancels or disposes
  a given object and the loser is a no-op. `StopAsync` and `Dispose` are now safe to call
  concurrently, repeatedly, and in either order.

  This surfaced most visibly in `WebApplicationFactory`-based integration tests, where xUnit
  reports a teardown exception as a test-class cleanup failure and fails the run.

  Note that a connection may still legitimately see more than one `Dispose` call: the connection
  loop owns its own reference and disposes it on cancellation, independently of either teardown
  path. `IDisposable.Dispose` is required to be idempotent, so this is safe.

## [0.5.0] - 2026-07-28

> **Hardware verification — complete.** The notification-payload decode described under "Fixed" was
> originally backed only by a decompile of `Beckhoff.TwinCAT.Ads 7.0.292`. It has since been
> exercised against a live TwinCAT runtime (`Plc30 App 3.1.2141`, TwinCAT/Linux) using the shipped
> library, across every decode path:
>
> | Symbol shape | Single read | Batch read (partition) | Notification |
> |---|---|---|---|
> | Scalar `INT` (PLC-driven) | ✓ | ✓ | ✓ values tracked the live variable |
> | `STRUCT` — enum + nested `STRUCT` + `REAL` | ✓ | ✓ identical tree | ✓ identical to a fresh read |
> | `ARRAY [0..3] OF INT` | ✓ `object?[]` | ✓ identical tree | ✓ identical to a fresh read |
>
> For both container shapes the decoded tree from `ReadValueWithMetadataAsync`, from a batch
> `ReadValuesAsync` (the container-partition branch), and from an `Action<AdsNotification>`
> subscription were identical, with `TypeName`/`Category` populated on each and a PLC-reported
> `Timestamp` on every notification. That equality is precisely the claim the "Fixed" entry makes.
>
> **Note on the packaged hardware suite.** `tests/Dahlke.TwinCAT.Ads.HardwareTests` cannot perform
> this verification on a host without a TwinCAT system router. Its fixture uses the code-first
> `AddTwinCatAds(o => ...)` overload and never sets `o.Router.NetId`, so `AdsRouterService` takes its
> "embedded router disabled — using system router" path and every fact fails on connection timeout
> regardless of PLC reachability. `AmsRouterOptions` exposes only `NetId`, so the code-first path
> cannot express `RemoteConnections` at all; the fixture needs the `IConfiguration` overload. The
> verification above was therefore performed with a standalone harness. Fixing the fixture is
> tracked for a follow-up release.

### Added

- `IAdsConnection.ReadValueWithMetadataAsync` — reads a symbol and returns an `AdsValueResult`
  carrying a decoded value tree plus the symbol's PLC `TypeName` and `Category`. Structs and
  function blocks decode to a `Dictionary<string, object?>` keyed by member name (assignable to
  `IReadOnlyDictionary<string, object?>`), arrays to `object?[]`, scalars pass through unchanged.
- `IAdsConnection.GetSymbolsAsync` / `SearchSymbolsAsync` and the `AdsSymbolInfo` record — browse
  and search the PLC symbol tree without depending on TwinCAT types. Browsing is bounded by the
  new `PlcTargetOptions.SymbolBrowseTimeoutMs` (default 30s), separate from `TimeoutMs` because it
  uploads the symbol table. The timeout bounds how long the caller waits; the underlying upload
  itself cannot be cancelled and keeps running on a background thread if the caller times out —
  its result is then discarded and any fault it raises is logged rather than left unobserved.
- `IAdsConnection.GetDeviceInfoAsync` and the `AdsDeviceInfo` record — device name and version. ADS
  state is deliberately excluded; read it via `GetAdsStateAsync`.
- `IAdsConnection.WriteControlAsync` — requests an ADS device state transition. The call completing
  means the device accepted the request, not that it finished transitioning; poll
  `GetAdsStateAsync` for the settled state.
- `SubscribeAsync(string, int, Action<AdsNotification>, CancellationToken)` and the
  `AdsNotification` record — notifications carrying the symbol path, decoded value, PLC type name
  and the PLC-reported timestamp. Durability across reconnects is identical to the existing
  overloads; see **Known limitations** below for how container symbols' delivery timing differs
  from scalars.
- `AdsValueResult.TypeName` and `AdsValueResult.Category`, plus a public
  `AdsValueResult.Success(value, symbolPath, typeName, category)` factory so a consumer writing a
  test double of `IAdsConnection.ReadValueWithMetadataAsync` can populate them — previously they
  were public to read but reachable only from internal factories, so every faked result reported
  `null`.
- `GetSymbolsAsync(string? parentPath, bool includeChildren, CancellationToken ct)` — the flag
  `SearchSymbolsAsync` already had. The existing two-argument overload keeps its current behaviour
  (children populated recursively), so nothing that compiles today changes meaning; pass
  `includeChildren: false` for interactive drill-down, which is what keeps a root browse from
  projecting every symbol on the PLC.

### Changed

- **Breaking:** `Beckhoff.TwinCAT.Ads` and `Beckhoff.TwinCAT.Ads.TcpRouter` now require
  `[7.0.292,8.0.0)` (previously `6.*`). The range is bracketed rather than floating: the
  notification-payload decode depends on deep public contract (`IValueRawSymbol.ValueAccessor` →
  `ValueFactory` → `CreateValue`) that a major version could reshape while preserving every
  signature, which would yield silently wrong values with no compile error and no test failure.
- `UNION` symbols now decode to a member-keyed tree like structs and function blocks, instead of
  reaching the caller as a raw TwinCAT `DynamicValue`. `Alias`, `Program`, `Pointer` and
  `Reference` are still passed through undecoded — now documented as such on
  `ReadValueWithMetadataAsync` rather than silently degrading.
- Symbol-tree walks (`GetSymbolsAsync`, `SearchSymbolsAsync`) stop at a symbol Beckhoff flags
  `IsRecursive` and at a hard depth ceiling of 32 levels. A truncated symbol is reported as a leaf.
- Simulated batch reads now carry `TypeName`/`Category` like a real connection, and
  `SimulatedAdsConnection.GetAdsStateAsync` now honours its cancellation token.
- Batch `ReadValuesAsync` now **partitions** by symbol category: scalars, strings and enums share a
  single ADS sum command as before, while structs, function blocks and arrays are decoded
  individually so their member values are returned as a tree rather than an opaque value. An
  all-scalar batch keeps its single round-trip; a batch containing containers costs that same
  round-trip for the scalars plus, per container symbol, either one read (arrays, and
  structs/function blocks with no sub-symbols) or — for structs/function blocks that do have
  sub-symbols — no top-level read at all, just one read per member, since a top-level read would
  only be discarded. Results carry `TypeName`/`Category` either way, and the whole batch (sum
  command, container reads, and every member read) shares one timeout/cancellation budget.
- Simulated symbol paths are now matched **case-insensitively**, matching real TwinCAT semantics.
  Previously a browse and a read disagreed on casing, so a mis-cased parent path could report a
  leaf as an empty container; browsing with a mis-cased path now also echoes back the symbol's
  originally-registered casing rather than the caller's.
- `SimulatedAdsConnection` implements every new member, so a fully simulated host can serve symbol
  browsing, device info, state writes and metadata reads with no TwinCAT installation.

### Fixed

- Notification handlers no longer read the symbol back over ADS to learn what changed. The value
  is decoded from the notification payload the PLC already sent, using the same value factory a
  read uses internally, removing a round-trip per event from the ADS notification thread. A symbol
  whose payload cannot serve on its own — for example one with external data references, or one
  without a decodable value source — falls back to a read instead; the fallback is logged once per
  subscription so a symbol that permanently loses the optimization stays visible rather than
  silent.
- `GetAdsStateAsync` and `IsAliveAsync` now check whether the ADS state read actually succeeded.
  Beckhoff's `ReadStateAsync` uses the non-throwing Result pattern, so a failed read completed
  normally and was treated as a success: `GetAdsStateAsync` returned `default(AdsState)` —
  indistinguishable from a state the device reported — and `IsAliveAsync` returned `true`, so an
  unreachable-but-connected PLC was reported healthy and never reconnected. `GetAdsStateAsync` now
  throws `AdsErrorException` (the exception it always documented); `IsAliveAsync` returns `false`,
  matching its own contract and the pool health loop's design.
- The untyped `SubscribeAsync` handler's logging is now failure-tolerant like the typed one's. It
  runs on the ADS notification thread, so a throwing logging provider escaped into Beckhoff's event
  dispatch, out of the `catch` that keeps a faulty callback from tearing the subscription down.
- A notification payload refusal that should be unreachable under `SymbolsLoadMode.DynamicTree`
  (`NoValueFactory`, `NotAValueSymbol`) is now logged at **Warning** naming the likely cause — a
  change in the Beckhoff client's symbol/value-accessor contract — rather than at Information
  alongside the benign external-data-references case.
- Struct decoding is now bounded by the operation's timeout and cancellation token. Previously each
  member read was unbounded and blocked a thread-pool thread, so a struct with many members could
  overrun `TimeoutMs` without failing.

### Known limitations

- Notifications for struct, function block and array symbols are decoded off the ADS notification
  thread, so they may be delivered slightly later than scalar notifications and may arrive out of
  order relative to them. Disposing a subscription cancels any in-flight decode and suppresses its
  delivery — but the guarantee is "never delivered after disposal completes," not "never delivered
  concurrently with disposal": a callback already running when `Dispose()` is called may still
  complete and fire.
- Container notifications carry a value and a timestamp that do **not** describe the same instant.
  The timestamp is the PLC's time for the change; the member reads that build the value run later,
  on the thread pool, and see whatever the PLC holds then. Under a burst the pair is incoherent.
  Scalar notifications have no such gap — their value comes from the notification's own payload.
- `Alias`, `Program`, `Pointer` and `Reference` symbols are not decoded into a neutral tree and
  reach the caller in Beckhoff's own shape. Treat a result whose `Category` is one of those as
  opaque. Routing them through the decoder was not attempted because whether a sub-symbol walk is
  meaningful differs per category and could not be established without hardware.
- `AdsConnection`'s device-level operations (`GetDeviceInfoAsync`, `WriteControlAsync`,
  `GetAdsStateAsync`/`IsAliveAsync` failure handling, subscription registration) have no unit-test
  coverage because the underlying `AdsClient` has no injection point; they are covered by the
  hardware test suite only.

## [0.4.0] - 2026-06-16

### Added

- **`Dahlke.TwinCAT.Ads.Reactive` companion package** — a separate, optional NuGet package exposing the callback-based subscription and connection-state APIs as `System.Reactive` `IObservable<T>` streams: `ObserveValue<T>`/`ObserveValue` and `ObserveConnectionState` on `IAdsConnection`, plus pool-level `ObserveValue<T>`/`ObserveValue` and `ObserveAllConnectionStates` on `IAdsConnectionPool`. Value streams are cold (each subscribe opens its own ADS notification, disposed on unsubscribe); connection-state streams are hot. The core package takes no dependency on `System.Reactive`. Includes the `Dahlke.TwinCAT.Ads.Examples.Reactive` example.

## [0.2.0] - Unreleased

### Breaking Changes

#### 1. `GetConnection` is non-nullable; unknown id throws `UnknownPlcTargetException`

**Before:**

```csharp
var connection = pool.GetConnection("plc1");
if (connection is null) return; // null-check required
```

**After:**

```csharp
// GetConnection always returns the stable facade — never null.
// It throws UnknownPlcTargetException (listing configured ids) for an unknown id.
var connection = pool.GetConnection("plc1");

// Non-throwing variant when the id may or may not be configured:
if (!pool.TryGetConnection("plc1", out var connection)) return;
```

#### 2. Batch methods return `IReadOnlyDictionary<string, AdsValueResult>`; write input is `IReadOnlyDictionary<string, object?>`

**Before:**

```csharp
// ReadValuesAsync returned IReadOnlyDictionary<string, object?>
var values = await connection.ReadValuesAsync(new[] { "GVL.A", "GVL.B" }, ct);
var a = (float)values["GVL.A"];

// WriteValuesAsync took IEnumerable<KeyValuePair<string, object>>
await connection.WriteValuesAsync(new[] {
    KeyValuePair.Create<string, object>("GVL.A", 1.5f)
}, ct);
```

**After:**

```csharp
// ReadValuesAsync returns IReadOnlyDictionary<string, AdsValueResult>
var results = await connection.ReadValuesAsync(["GVL.A", "GVL.B"], ct);
foreach (var (symbol, result) in results)
{
    if (result.Succeeded)
        Console.WriteLine($"{symbol} = {result.Value}");
    else
        Console.WriteLine($"{symbol} FAILED: {result.Error!.Message}");
}
// Typed access on a result:
float temp = results["GVL.Temp"].GetValue<float>();

// WriteValuesAsync takes IReadOnlyDictionary<string, object?>
await connection.WriteValuesAsync(new Dictionary<string, object?>
{
    ["GVL.A"] = 1.5f,
    ["GVL.B"] = true,
}, ct);
```

Real connections execute both operations as a single ADS sum command (one round-trip). A whole-batch timeout throws `TimeoutException`; per-symbol failures are captured in `AdsValueResult.Error` and do not throw.

#### 3. `SubscribeAsync<T>` has no optional `CancellationToken` default

**Before:**

```csharp
// Optional ct — could omit
var sub = await connection.SubscribeAsync<float>("GVL.Temp", 200, OnValue);
```

**After:**

```csharp
// ct is required — pass CancellationToken.None explicitly
var sub = await connection.SubscribeAsync<float>("GVL.Temp", 200, OnValue, CancellationToken.None);
```

#### 4. Types removed from the public surface

The following types are no longer public. Replace any direct usage with the interfaces and extension points they backed:

| Removed type | Replacement |
|---|---|
| `AdsConnection` | `IAdsConnection` (from `pool.GetConnection(id)`) |
| `AdsRouterService` | Registered internally; no direct instantiation needed |
| `AdsRouterReadySignal` | Internal implementation detail |
| `SimulatedAdsConnectionPool` | Deleted. Use `AddTwinCatAdsSimulation` or per-target `Mode = Simulated` |
| `IAdsConnectionFactory` | Internal; not part of the public contract |

`SimulatedAdsConnection` remains public for test-code seeding via `pool.TryGetSimulatedConnection`.

#### 5. `AddTwinCatAdsSimulation` no longer registers a separate pool

`AddTwinCatAdsSimulation` is now sugar over `AddTwinCatAds`: it registers the identical core services and appends a `PostConfigure` delegate that forces every target to `ConnectionMode.Simulated`. There is no longer a separate `SimulatedAdsConnectionPool` type. Mixed fleets (some targets real, some simulated) are configured via per-target `Mode` rather than by choosing a different pool type.

#### 6. `TimeoutException` vs `OperationCanceledException` are no longer conflated

All operations now throw `TimeoutException` when the per-target `TimeoutMs` elapses, and `OperationCanceledException` only when the caller's `CancellationToken` fires. Previously both were mapped to `OperationCanceledException`. Update `catch` blocks that need to distinguish the two cases.

#### 7. Reads are genuinely asynchronous and honour cancellation and timeout

Single-symbol reads (`ReadValueAsync`) were previously executed synchronously on the calling thread. They are now fully asynchronous. Cancellation and `TimeoutMs` are honored consistently across all read and write operations.

### Added

- **`Iec61131Converter`** — table-driven mapping between IEC 61131-3 elementary type names and .NET types, with default-value lookup and value conversion. A strict standard core (canonical uppercase names, case-sensitive) plus a lenient, case-insensitive `Iec61131Converter.Beckhoff` tier that recognises Beckhoff aliases (`dtSystemTime`, `T_UD`, `BIT`, `BIT8`) and delegates to the core. Conversion reuses the shared invariant-culture conversion core.
- **Stable per-target connection facade** — `GetConnection` returns one object whose identity never changes for the pool's lifetime. Reconnects are invisible to callers; cached references never go stale. Operations during an outage wait up to `TimeoutMs` for reconnection and then throw `AdsConnectionUnavailableException`; operations fail fast after the pool stops.
- **`TryGetConnection`** — non-throwing lookup that returns `false` when the id is not configured.
- **`TryGetSimulatedConnection`** — test-support escape hatch to retrieve the live `SimulatedAdsConnection` for seeding initial values in code-first tests.
- **Durable subscriptions** — subscriptions survive reconnects automatically. The returned `IDisposable` stays valid through outages; the callback resumes firing once the connection is re-established. Disposing the handle removes the subscription permanently.
- **`ConnectionStateChanged` event and `State` property** — reactive and observational connection-state on `IAdsConnection`. Enables outage-gap detection without polling.
- **Typed API** — `ReadValueAsync<T>`, `WriteValueAsync<T>`, and `SubscribeAsync<T>` with automatic widening conversions and invariant-culture string parsing. The untyped `object?` overloads remain as a dynamic escape hatch.
- **Per-target `ConnectionMode`** (`Real` | `Simulated`) with `InitialValues` seeding. Mixed fleets (some targets real, some simulated) are supported in a single registration.
- **Simulated subscriptions now fire on changed writes** — writing a new value to a simulated symbol triggers registered callbacks immediately (on-change semantics). Previously subscriptions on simulated connections were accepted but never fired.
- **Code-first registration** — `AddTwinCatAds(o => ...)` without `IConfiguration`; combo overload `AddTwinCatAds(IConfiguration, Action<TwinCatAdsOptions>)` applies config binding first, lambda second.
- **Options validation at startup** (`ValidateOnStart`) — malformed `AmsNetId` values, invalid ports, and non-positive timeouts produce actionable error messages at boot rather than at first use.
- **Health check** — `services.AddHealthChecks().AddTwinCatAdsHealthCheck()` exposes Healthy / Degraded / Unhealthy with per-target data. Degraded when at least one real target is disconnected; Unhealthy when all real targets are down.
- **ADS sum commands** — batch read and write on real connections are executed as a single ADS sum command (one round-trip) rather than one request per symbol.
- **`AdsSymbolDump` configuration section** — `{Enabled, MaxDepth, Prefixes}` replaces the legacy `AdsSymbolTreeDump` boolean. The legacy key is still honoured for backward compatibility; the new section takes precedence.
- **XML documentation on all public APIs** and PublicAPI analyzer enforcement.
- **Opt-in hardware integration test suite** — `Dahlke.TwinCAT.Ads.HardwareTests` project; skipped when no hardware is present.
- **Multi-targeted unit tests** — test suite runs against all three supported TFMs (.NET 8, 9, 10).

### Changed

- **Embedded router retry** — the router now retries with exponential backoff (2 s → 30 s cap) instead of giving up after one failure. Pool startup never blocks on the router; simulated targets start instantly and real targets join automatically once the router is ready.
- **`AdsConnection`, `AdsRouterService`, `AdsRouterReadySignal`, `IAdsConnectionFactory`** demoted to `internal`. See the Breaking Changes section for the removal table.
- **`SimulatedAdsConnectionPool` deleted** — superseded by per-target `Mode = Simulated` plus `AddTwinCatAdsSimulation` sugar.

### Fixed

- Cancellation and timeout were previously conflated — both surfaced as `OperationCanceledException`. Timeout now surfaces as `TimeoutException` (see Breaking Changes §6).
- Single-symbol reads were synchronous on the calling thread and could not be cancelled mid-flight. They are now fully asynchronous (see Breaking Changes §7).
- Subscriptions on simulated connections were silent no-ops. They now fire on change (see Added).

## [0.1.0] - 2026-04-10

### Added

- Connection pooling with `IAdsConnectionPool` for managing multiple PLC connections
- Automatic reconnection with exponential backoff (2s–30s) and periodic health checks
- Embedded ADS TCP/IP router support via `AdsRouterService`
- Symbol read/write operations (single and batch) with configurable timeouts
- Device notification subscriptions for real-time PLC data
- Simulation mode with in-memory key-value store for offline development
- ASP.NET Core integration via `AddTwinCatAds()` and `AddTwinCatAdsSimulation()` extension methods
- Multi-target support for .NET 8.0, 9.0, and 10.0
- CI pipeline with build and test across all target frameworks
- NuGet release pipeline triggered by version tags
- Apache 2.0 license

[0.1.0]: https://github.com/patdhlk/Dahlke.TwinCAT.Ads/releases/tag/v0.1.0
