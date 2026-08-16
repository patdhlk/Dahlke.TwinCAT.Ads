# Connections

How connections are created, looked up, observed and bounded — with a generic host, and without
one. For registering targets in the first place, see the
[quick start](https://github.com/patdhlk/Dahlke.TwinCAT.Ads#quick-start) and the
[configuration reference](configuration.md).

## Stable connection facades

`GetConnection` returns one object per target whose identity never changes: reconnects are
invisible, and cached references never go stale. Everything on this page builds on that.

## Console, WPF and WinForms — no host required

The DI registrations from the quick start are right for ASP.NET Core and worker services. For a
commissioning tool or an HMI, `AdsConnectionPoolBuilder` gives the same pool with no generic host:

```csharp
await using var pool = await AdsConnectionPoolBuilder
    .Create()
    .AddTarget("plc1", o => { o.AmsNetId = "192.168.1.10.1.1"; o.Port = 851; })
    .BuildAndStartAsync();

var conn = pool.GetConnection("plc1");
```

This is a face on the DI path, not a second implementation: the builder stands up a private
`ServiceCollection`, calls the very same `AddTwinCatAds`, and starts what comes out. So the five
things this library does — options validation, the embedded router, reconnection, health tracking
and raw channels — behave here exactly as they do in a hosted application, because they are the
same registrations running the same code. What the private container is *not* is a generic host;
see [Companion packages](#companion-packages) for where that shows.

`CreateSimulation()` is the `AddTwinCatAdsSimulation` equivalent — every target in memory, no
TwinCAT installation:

```csharp
await using var pool = await AdsConnectionPoolBuilder
    .CreateSimulation()
    .AddTarget("plc1", o => o.InitialValues["GVL.Temp"] = 21.5f)
    .BuildAndStartAsync();
```

The returned handle **is** an `IAdsConnectionPool`, and also exposes `RawChannels` and `Services`.

| Builder method | What it does |
|---|---|
| `AddTarget(id, configure)` | Configures one target, creating it if needed. Two calls for one id compose. |
| `Configure(configure)` | Applies a delegate to the whole options tree — router, diagnostics, raw channels. |
| `UseConfiguration(configuration)` | Binds the usual sections first, before any `Configure`/`AddTarget` lambda. |
| `UseLoggerFactory(factory)` | Routes logs somewhere. Without it the pool is silent. |
| `ConfigureServices(configure)` | Adds services to the private container — including companion packages. |

### Waiting for a real target

`BuildAndStartAsync` returns as soon as the pool is started. A **simulated** target is connected at
that point. A **real** target is not: its connection loop is deferred until the embedded router is
ready, which is retried with backoff. So a tool that reads immediately gets
`AdsConnectionUnavailableException` after `TimeoutMs`.

```csharp
if (!await conn.WaitForConnectedAsync(TimeSpan.FromSeconds(30)))
{
    Console.Error.WriteLine("PLC did not come up.");
    return 1;
}
```

`WaitForConnectedAsync` is an extension on `IAdsConnection`, so it works just as well on a
connection resolved from DI during host startup.

### Companion packages

`ConfigureServices` is the seam. Anything registered there is built with everything else. An
`IHostedService` registered there — the alarm monitor `AddTwinCatAdsAlarms` registers (see
[PLC alarms](alarms.md)) — is deliberately moved to start **after** the router, pool and raw
channels, matching a generic host where `AddTwinCatAds(...)` is called before
`AddTwinCatAdsAlarms(...)`, so a companion package can assume the pool it depends on is already
connected:

```csharp
var alarmsConfig = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .Build();

await using var pool = await AdsConnectionPoolBuilder
    .Create()
    .AddTarget("plc1", o => o.AmsNetId = "192.168.1.10.1.1")
    .ConfigureServices(s => s.AddTwinCatAdsAlarms(alarmsConfig))
    .BuildAndStartAsync();

var monitor = pool.Services.GetRequiredService<IPlcAlarmMonitor>();
```

**The private container is a hosted-service runner, not a host.** It calls `StartAsync` and
`StopAsync` and nothing else. Four things a generic host would do are absent:

- `IHostedLifecycleService`'s `StartingAsync`/`StartedAsync`/`StoppingAsync`/`StoppedAsync` are never invoked, even on a service that implements the interface.
- `BackgroundServiceExceptionBehavior` is not honoured. A `BackgroundService` whose `ExecuteAsync` faults after startup faults only its own task, which nothing observes — so the application keeps running where the host default, `StopHost`, would have brought it down.
- The provider is built without `ValidateScopes` or `ValidateOnBuild`, so a captive dependency or an unresolvable registration surfaces at first resolve rather than at build.
- Shutdown is unbounded — there is no `HostOptions.ShutdownTimeout` here, so a service that hangs in `StopAsync` hangs `DisposeAsync` with it.

The first two are unreachable through this library's own services or `Dahlke.TwinCAT.Ads.Alarms`:
the alarm monitor, the pool and the raw-channel factory are plain `IHostedService` implementations
with no lifecycle hooks, and the embedded router — the one `BackgroundService` among them — wraps
the whole of its `ExecuteAsync` in a catch-all, so its task never faults. All four matter for a
service *you* register through `ConfigureServices`.

### Shutting down

The handle is `IAsyncDisposable` and not `IDisposable`, because stopping the router and the
reconnect loops is genuinely asynchronous. `await using` is the intended shape. A WinForms or WPF
shutdown path that cannot `await` should block explicitly rather than leave the pool running:

```csharp
protected override void OnExit(ExitEventArgs e)
{
    _pool.DisposeAsync().AsTask().GetAwaiter().GetResult();
    base.OnExit(e);
}
```

## Connection lookup

```csharp
// Always returns the stable facade for a configured target — never null.
// Throws UnknownPlcTargetException (listing configured ids) for an unknown id.
var conn = pool.GetConnection("plc1");

// Non-throwing variant:
if (!pool.TryGetConnection("plc1", out var conn))
    return Results.NotFound("Unknown PLC.");

// Enumerate all targets:
foreach (var (plcId, conn) in pool.GetAllConnections())
    Console.WriteLine($"{plcId} ({conn.DisplayName}) connected: {conn.IsConnected}");
```

### Keyed injection — one target, named once

A service that talks to a single PLC can name it at the injection point instead of at every lookup:

```csharp
public class TempService([FromKeyedServices("plc1")] IAdsConnection conn)
{
    public Task<double> ReadAsync() => conn.ReadValueAsync<double>("MAIN.rTemperature");
}
```

Every configured target is resolvable this way, from all six `AddTwinCatAds` /
`AddTwinCatAdsSimulation` overloads — including targets added by a
`services.Configure<TwinCatAdsOptions>(...)` call made *after* registration. The keyed connection
**is** the pool's facade, not a copy: `sp.GetRequiredKeyedService<IAdsConnection>("plc1")` and
`pool.GetConnection("plc1")` return the same instance, so a subscription taken through one is
visible to the other. Keys are case-insensitive, exactly as `GetConnection` is.

An unknown id fails at container resolution with `UnknownPlcTargetException` listing every
configured id, rather than at the first PLC call. **`GetKeyedService` throws too** rather than
returning `null` — the registration is a factory, so it runs on the optional path as well. Use
`pool.TryGetConnection` when the id may legitimately not be configured.

A test can substitute a single target by registering an explicit key, which takes precedence over
the keyed registration while leaving every other target real:

```csharp
services.AddKeyedSingleton<IAdsConnection>("plc1", new StubConnection());  // plc2 unaffected
```

`IAdsConnectionPool` is unchanged and remains the right answer for code that works across
targets — and the only way to **enumerate** them.
`GetKeyedServices<IAdsConnection>(KeyedService.AnyKey)` does not work for that: on .NET 8 and 9 it
throws (the container passes the `AnyKey` sentinel to the factory, and a sentinel names no
target), and on .NET 10 it returns empty. Use `pool.GetAllConnections()`. Injecting
`IEnumerable<IAdsConnection>` likewise yields nothing — the keyed registration deliberately does
not leak into the unkeyed enumeration.

## Connection state

```csharp
// Observational snapshot — a hint, not a guard.
bool up = conn.IsConnected;
ConnectionState state = conn.State; // Disconnected | Connecting | Connected

// Reactive notification:
conn.ConnectionStateChanged += (_, e) =>
    Console.WriteLine($"{e.PlcId}: {e.PreviousState} → {e.State}");
```

`IsConnected` and `State` are snapshots. Operation methods do not consult them; they apply the
[wait-then-throw contract](#wait-then-throw-semantics) directly.

`Connected` means the link has been **proven by a real ADS round trip** — the pool issues a
`ReadState` probe after `Connect()` and only publishes the connection when the probe answers, then
keeps it honest with a periodic health check. A locally "connected" socket whose peer is
unreachable (dead route, misconfigured `AmsNetId`, cable pulled) reports `Disconnected`, not a
false green.

For dashboards and status endpoints, the pool exposes a per-target snapshot:

```csharp
IReadOnlyList<PlcTargetStatus> states = pool.GetTargetStates();
// [ PlcTargetStatus { PlcId = "plc1", Mode = Real, State = Connected }, ... ]
```

The Rx companion exposes the same information as `IObservable<T>` streams — see
[Subscriptions](subscriptions.md#reactive-rx-companion).

## Wait-then-throw semantics

When no live connection is available (connecting, mid-outage), every operation on an
`IAdsConnection` waits up to the target's `TimeoutMs` milliseconds for a connection to be
published, then throws `AdsConnectionUnavailableException`. After the pool is stopped (host
shutdown), operations fail fast without waiting.

`TimeoutException` is thrown when the hardware round-trip exceeds `TimeoutMs`.
`OperationCanceledException` is thrown only when the caller's `CancellationToken` fires. The two
are never conflated.

## Per-call timeouts — `WithTimeout`

`TimeoutMs` is per **target**, so one slow symbol, one long-running RPC or one large struct decode
would otherwise mean raising the bound for every operation on that PLC. `WithTimeout` scopes the
change to the calls that need it:

```csharp
var slow = conn.WithTimeout(TimeSpan.FromSeconds(30));
var value = await slow.ReadValueAsync<float>("GVL.BigStruct");

// or inline, for a one-off:
await conn.WithTimeout(TimeSpan.FromMinutes(2)).SearchSymbolsAsync("Motor", includeChildren: false);
```

The returned value is a lightweight view, not a second connection: it shares the target's
identity, state, `ConnectionStateChanged` event, transport and subscriptions. Nothing is opened
and nothing needs disposing.

Three things worth knowing:

- **It replaces both configured bounds.** Operations that would use `TimeoutMs` and browses that would use `SymbolBrowseTimeoutMs` both use the scope, so one `WithTimeout` covers every operation the view performs rather than silently exempting the slowest. A scope built for a quick read and then reused for a browse therefore **shortens** that browse, which by default is allowed six times longer.
- **It bounds the wait for a connection too.** Wait-then-throw keeps its shape, but a scoped call waits the scope — not `TimeoutMs` — before throwing `AdsConnectionUnavailableException`, so a long scope also lengthens how long that one call parks during an outage.
- **Scopes replace rather than nest.** `conn.WithTimeout(a).WithTimeout(b)` is bounded by `b`.

For subscriptions the scope bounds the **registration** call — the only part that waits. The
subscription itself is as durable as any other, and the timeout does not follow it into the
callbacks. On a simulated target only the wait-for-connection half is observable, since a
simulated operation completes against an in-memory store with no bound to change.
