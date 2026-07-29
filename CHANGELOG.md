# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.6.0] - 2026-07-29

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
  `AdsRawChannelOptions.Seed` key **fails the host at startup** instead: a seed key is a
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

  Seed keys and payloads are validated at startup in **both** modes, so a malformed entry left
  behind after a switch to `Real` still fails the host rather than sitting silently broken.

  **`AdsRawChannelOptions` is configured in code, not from `IConfiguration`.** Unlike
  `PlcTargets`, no binding for a `RawChannels` section is wired up in this release: set
  `TwinCatAdsOptions.RawChannels` through an `AddTwinCatAds(…)` options delegate. A
  `"RawChannels"` block in `appsettings.json` is read by nothing.

### Changed

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

### Fixed

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
