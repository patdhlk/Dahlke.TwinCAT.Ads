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

## Running locally

```bash
export TWINCAT_HARDWARE_TESTS=1
export TWINCAT_TEST_AMSNETID=192.168.1.10.1.1
export TWINCAT_TEST_PORT=851
export TWINCAT_TEST_SYMBOL_INT=MAIN.TestInt
export TWINCAT_TEST_SYMBOL_STRUCT=MAIN.TestStruct
export TWINCAT_TEST_SYMBOL_ARRAY=MAIN.TestArray

dotnet test tests/Dahlke.TwinCAT.Ads.HardwareTests --framework net8.0
```

> Facts gated on an unset symbol variable return immediately and report as **passed**, not skipped.
> A run that leaves `TWINCAT_TEST_SYMBOL_STRUCT` / `TWINCAT_TEST_SYMBOL_ARRAY` unset is therefore
> green while verifying nothing about containers — set all three before treating a green run as
> release evidence.

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

## CI

The hardware test project is included in the solution but the tests are gated by environment variables. In CI (`TWINCAT_HARDWARE_TESTS` and `TWINCAT_TEST_AMSNETID` are never set), all tests show as **Skipped** — the build and test steps do not fail.
