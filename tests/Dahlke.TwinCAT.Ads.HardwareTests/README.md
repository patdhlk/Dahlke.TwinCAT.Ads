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

## Running locally

```bash
export TWINCAT_HARDWARE_TESTS=1
export TWINCAT_TEST_AMSNETID=192.168.1.10.1.1
export TWINCAT_TEST_PORT=851
export TWINCAT_TEST_SYMBOL_INT=MAIN.TestInt

dotnet test tests/Dahlke.TwinCAT.Ads.HardwareTests --framework net8.0
```

## Test coverage

| Test | What it verifies |
|---|---|
| `HostStarted_ConnectionIsAvailableAndConnected` | `AddTwinCatAds` + host start → connect → `IsConnected=true` |
| `TypedReadWrite_RoundTrip_IntSymbol` | Typed `ReadValueAsync<T>` / `WriteValueAsync<T>` round-trip |
| `UntypedRead_ReturnsNonNullValue_ForConfiguredIntSymbol` | Untyped `ReadValueAsync` returns a non-null boxed value |
| `BatchRead_GoodAndBogusSymbol_BogusIsFailure_GoodSucceeds` | Batch sum-command: one bogus symbol → `DeviceSymbolNotFound` failure; good symbol succeeds (C20/C21 real-divergence check) |
| `BatchWrite_IntSymbol_Succeeds` | Batch `WriteValuesAsync` + read-back round-trip |
| `GetAdsStateAsync_ReturnsRunOrConfig` | `GetAdsStateAsync` returns a plausible ADS state |
| `Subscribe_OnChange_ReceivesNotification` | Untyped subscription fires on value change |
| `SubscribeTyped_OnChange_ReceivesTypedNotification` | Typed `SubscribeAsync<T>` fires with correct type |
| `HealthCheck_LivePool_ReturnsHealthy` | Health check against live pool → `Healthy` |

## CI

The hardware test project is included in the solution but the tests are gated by environment variables. In CI (`TWINCAT_HARDWARE_TESTS` and `TWINCAT_TEST_AMSNETID` are never set), all tests show as **Skipped** — the build and test steps do not fail.
