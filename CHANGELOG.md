# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
