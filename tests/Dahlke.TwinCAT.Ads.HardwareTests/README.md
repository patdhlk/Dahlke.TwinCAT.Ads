# Dahlke.TwinCAT.Ads.HardwareTests

Opt-in integration tests that run against a live TwinCAT 3 runtime. They are **skipped by default** in all CI and local builds unless you explicitly opt in via environment variables.

## Prerequisites

- A TwinCAT 3 runtime reachable over ADS from the machine running the tests
- The ADS router must be running and the route to the PLC configured
- .NET 8 SDK or later

> **Note on target frameworks:** unlike the unit-test project (which multi-targets
> net8.0/net9.0/net10.0 per CONTRIBUTING), this project deliberately targets
> **net8.0 only**. The hardware boundary under test — the Beckhoff ADS transport —
> is framework-independent, and tripling runs against a physical PLC adds wall-clock
> time without adding coverage. Cross-framework verification is the unit suite's job.

## Enabling the tests

Set **one or both** of these environment variables before running `dotnet test`:

| Variable | Purpose |
|---|---|
| `TWINCAT_HARDWARE_TESTS=1` | Master gate — enables all hardware tests |
| `TWINCAT_TEST_AMSNETID` | AMS Net ID of the target PLC (e.g. `192.168.1.10.1.1`) — setting this also gates the tests |

If neither variable is set every test shows as **Skipped** — no failure, no connection attempt.

## Configuration variables

| Variable | Default | Description |
|---|---|---|
| `TWINCAT_TEST_AMSNETID` | *(required)* | AMS Net ID of the target PLC |
| `TWINCAT_TEST_PORT` | `851` | ADS port of the first PLC runtime |
| `TWINCAT_TEST_SYMBOL_INT` | *(optional)* | Fully-qualified path of a **writable INT** symbol (e.g. `MAIN.TestInt`). Tests that require a symbol are skipped inline if this is not set. |
| `TWINCAT_TEST_SYMBOL_STRUCT` | *(optional)* | Fully-qualified path of a **STRUCT or FUNCTION_BLOCK** symbol (e.g. `MAIN.TestStruct`). Read-only — nothing writes it — but it must be **stable for the run**: the container facts compare a notification's decoded tree against a fresh read, which is meaningless for a symbol the PLC program is continuously mutating. |
| `TWINCAT_TEST_SYMBOL_ARRAY` | *(optional)* | Fully-qualified path of an **ARRAY** symbol (e.g. `MAIN.TestArray`). Same read-only/stable requirement as the struct. This is the highest-value probe: an array notification is the only container whose raw value comes from the payload decode rather than from per-member reads. |
| `TWINCAT_TEST_SYMBOL_ALARMS` | *(optional)* | Fully-qualified path of an alarm array symbol (`ARRAY[..] OF ST_ErrorEntry`, e.g. `MAIN.ErrorHandler.aHmiAlarms`). Unlike the struct/array symbols above it need NOT be stable — the alarm test asserts the array *binds*, not that a notification matches a re-read, so a live alarm list is a fine target. **Give the path a parent segment**: acknowledgement derives the owning function block by trimming this path's last segment, so `GVL.Errors` would derive `GVL`, which owns no function block. Nothing catches that until an acknowledgement is attempted, which the current alarm test never does — but a test that does will. The alarm test returns early (and reports as passed, not skipped) when this is unset. |
| `TWINCAT_TEST_ALARM_ACK_KEY` | *(optional)* | The `sKey` of an alarm the acknowledge test may acknowledge for real (e.g. `Test_Err_60`). **This is the only hardware variable that causes a write to the PLC** — every other variable in this table is read-only as far as these tests are concerned. Point it at a test alarm, not a production one. Requires `TWINCAT_TEST_SYMBOL_ALARMS` too; the acknowledge test returns early (and reports as passed, not skipped) when either is unset. |
| `TWINCAT_TEST_ROUTER_NETID` | *(optional)* | This host's AMS Net ID for the embedded router (e.g. `192.168.1.220.1.1`). When unset, `Router` is left untouched and the tests connect through the **system router** — which exists on Windows and nowhere else. |
| `TWINCAT_TEST_ROUTE_ADDRESS` | *(optional)* | The target PLC's IP address or host name (e.g. `192.168.1.223`), used to build the single route from the embedded router to `TWINCAT_TEST_AMSNETID`. |

> **Reaching a PLC from a machine without a TwinCAT installation.** The system router — used
> whenever `TWINCAT_TEST_ROUTER_NETID` / `TWINCAT_TEST_ROUTE_ADDRESS` are unset — only exists on
> Windows with TwinCAT installed. On any other machine (Linux, macOS, a plain Windows box, a CI
> runner), set **both** router variables so the tests configure the embedded router with a route
> to the target instead; see `HardwareTestConfig.HasEmbeddedRouter`. Setting only one of the two
> is equivalent to setting neither — a router with no route to the target is useless.

## Running locally

```bash
export TWINCAT_HARDWARE_TESTS=1
export TWINCAT_TEST_AMSNETID=192.168.1.10.1.1
export TWINCAT_TEST_PORT=851
export TWINCAT_TEST_SYMBOL_INT=MAIN.TestInt
export TWINCAT_TEST_SYMBOL_STRUCT=MAIN.TestStruct
export TWINCAT_TEST_SYMBOL_ARRAY=MAIN.TestArray
export TWINCAT_TEST_SYMBOL_ALARMS=MAIN.ErrorHandler.aHmiAlarms

# The only variable here that causes a WRITE to the PLC. Without it the acknowledge test
# reports passed while doing nothing — see the note below.
export TWINCAT_TEST_ALARM_ACK_KEY=Test_Err_60

dotnet test tests/Dahlke.TwinCAT.Ads.HardwareTests --framework net8.0
```

### Example: a non-Windows host (no system router)

This is the configuration verified against a live rack from a host with no TwinCAT installation.
The router variables are what make it work at all — without them the tests can only reach a PLC
through a system router, i.e. **Windows only**. Values below are this one site's example; do not
hardcode them, set them in the environment that runs the tests.

```bash
export TWINCAT_HARDWARE_TESTS=1

# This host's own AMS Net ID and the route to the target — the embedded router.
export TWINCAT_TEST_ROUTER_NETID=192.168.1.220.1.1
export TWINCAT_TEST_ROUTE_ADDRESS=192.168.1.223

# The target PLC.
export TWINCAT_TEST_AMSNETID=5.138.44.199.1.1
export TWINCAT_TEST_PORT=851
export TWINCAT_TEST_SYMBOL_ALARMS=MAIN.ErrorHandler.aHmiAlarms
export TWINCAT_TEST_ALARM_ACK_KEY=Test_Err_60

dotnet test tests/Dahlke.TwinCAT.Ads.HardwareTests --framework net8.0
```

> Facts gated on an unset symbol variable return immediately and report as **passed**, not skipped.
> A run that leaves `TWINCAT_TEST_SYMBOL_STRUCT` / `TWINCAT_TEST_SYMBOL_ARRAY` /
> `TWINCAT_TEST_SYMBOL_ALARMS` / `TWINCAT_TEST_ALARM_ACK_KEY` unset is therefore green while
> verifying nothing about containers, alarm binding, **or acknowledgement** — set all five before
> treating a green run as release evidence. `TWINCAT_TEST_ALARM_ACK_KEY` is the one that gates
> `Acknowledge_ReachesThePlc`, the only test that exercises 0.7.0's headline mechanism; a run
> without it proves nothing about acknowledgement no matter how green it looks. The first block
> above sets all five for exactly that reason; the second sets only the alarm pair, because what
> it records is one site's router configuration rather than a full release run.

## Test coverage

| Test | What it verifies |
|---|---|
| `HostStarted_ConnectionIsAvailableAndConnected` | `AddTwinCatAds` + host start → connect → `IsConnected=true` |
| `TypedReadWrite_RoundTrip_IntSymbol` | Typed `ReadValueAsync<T>` / `WriteValueAsync<T>` round-trip |
| `UntypedRead_ReturnsNonNullValue_ForConfiguredIntSymbol` | Untyped `ReadValueAsync` returns a non-null boxed value |
| `BatchRead_GoodAndBogusSymbol_BogusIsFailure_GoodSucceeds` | Batch sum-command: one bogus symbol → `DeviceSymbolNotFound` failure; good symbol succeeds (C20/C21 real-divergence check) |
| `BatchWrite_IntSymbol_Succeeds` | Batch `WriteValuesAsync` + read-back round-trip |
| `GetAdsStateAsync_ReturnsRunOrConfig` | `GetAdsStateAsync` returns a plausible ADS state |
| `Subscribe_OnChange_DeliversTheValueThatWasWritten` | Untyped subscription fires on change **and delivers the value that was written** — not merely a non-null one |
| `SubscribeTyped_OnChange_ReceivesTypedNotification` | Typed `SubscribeAsync<T>` fires with correct type |
| `SubscribeNotification_OnChange_CarriesValueTypeNameAndTimestamp` | `Action<AdsNotification>` overload: asserts `Value`, `TypeName` and `Timestamp` |
| `SubscribeNotification_StructSymbol_DecodesToTheSameTreeAsARead` | Struct notification decodes to the same tree a read returns (needs `TWINCAT_TEST_SYMBOL_STRUCT`) |
| `SubscribeNotification_ArraySymbol_DecodesToTheSameTreeAsARead` | Array notification decodes to the same tree a read returns — the payload-decode divergence check (needs `TWINCAT_TEST_SYMBOL_ARRAY`) |
| `ReadValueWithMetadata_StructSymbol_DecodesToAKeyedTree` | `ReadValueWithMetadataAsync` on a struct → keyed tree + `TypeName`/`Category` |
| `ReadValueWithMetadata_ArraySymbol_DecodesToAnObjectArray` | `ReadValueWithMetadataAsync` on an array → `object?[]` + `TypeName`/`Category` |
| `HealthCheck_LivePool_ReturnsHealthy` | Health check against live pool → `Healthy` |
| `AlarmArray_BindsAgainstRealHardware` | `Dahlke.TwinCAT.Ads.Alarms.PlcAlarmBinder` binds a real alarm-array notification without throwing (needs `TWINCAT_TEST_SYMBOL_ALARMS`) |
| `Acknowledge_ReachesThePlc` | `AcknowledgeAsync` invokes the PLC's `AcknowledgeAlarm` method by RPC and resolves `deaReturnType` by name — the only test that proves acknowledgement reaches the PLC (needs `TWINCAT_TEST_SYMBOL_ALARMS` and `TWINCAT_TEST_ALARM_ACK_KEY`) |

> **`AlarmArray_BindsAgainstRealHardware` is deliberately hard to pass vacuously.** An empty
> outstanding-alarm set is a legitimate PLC state, but by itself it looks identical to "the binder
> threw and the whole snapshot was silently dropped" — `PlcAlarmMonitor` catches
> `PlcAlarmShapeException`, logs it, and keeps the store's last good (empty, on a fresh host)
> reading. So besides asserting on every bound alarm's fields (`Key`, `PlcId`, `SlotIndex`, and
> `Severity` being one of the four known `AlarmSeverity` values), the test also opens its own raw
> subscription to the same symbol and fails if zero notifications arrived, and captures
> `PlcAlarmMonitor`'s Error-level log output and fails if any was recorded — both checks fire
> regardless of whether the array is empty. It does **not** prove that acknowledgement reaches
> the PLC — nothing here calls `AcknowledgeAsync`; that path is covered separately by
> `Acknowledge_ReachesThePlc`, since acknowledgement is not a write: it invokes the PLC method
> named by `AcknowledgeMethod` on the function block derived from `SymbolPath` and resolves the
> returned `deaReturnType` by name, so the assertions have to be on the call, its result, and the
> array afterwards no longer carrying the alarm. And — like every other symbol-gated test here —
> it still reports **passed while doing nothing** when `TWINCAT_TEST_SYMBOL_ALARMS` is unset, even
> with `TWINCAT_HARDWARE_TESTS=1` set.
>
> **`Acknowledge_ReachesThePlc` writes to the PLC.** It is gated separately on
> `TWINCAT_TEST_ALARM_ACK_KEY` in addition to `TWINCAT_TEST_SYMBOL_ALARMS`, and reports **passed
> while doing nothing** when either is unset. On hardware a successful acknowledge *removes* the
> entry from the array rather than setting `IsAcked` — the alarm ends, so the store emits `Ended`,
> never `Acknowledged` — and the rig re-fires test alarms every second or two, so an alarm can
> legitimately end between the test reading it and the call landing. The assertion therefore
> accepts either `AcknowledgeAsync` returning `true` or the alarm no longer being outstanding; only
> a thrown `PlcAlarmAcknowledgeException` counts as failure.

## CI

The hardware test project is included in the solution but the tests are gated by environment variables. In CI (`TWINCAT_HARDWARE_TESTS` and `TWINCAT_TEST_AMSNETID` are never set), all tests show as **Skipped** — the build and test steps do not fail.
