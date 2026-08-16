# Testing

`Dahlke.TwinCAT.Ads.Testing` packages what every consumer previously rebuilt: a started pool with
simulated targets, seeded, ready to inject — plus a record of what the code under test wrote. For
the simulation layer it builds on, see [Simulation](simulation.md).

```bash
dotnet add package Dahlke.TwinCAT.Ads.Testing
```

It depends on the core package and on **no test framework**, so it works from xunit, NUnit,
MSTest or a plain console harness.

```csharp
await using var plc = await TestPlc.Create()
    .WithTarget("plc1", seed => seed["GVL.Temp"] = 21.5f)
    .StartAsync();

var sut = new TempService(plc.Pool);

plc.Target("plc1").Write("GVL.Temp", 30f);          // drive the system under test
await sut.ProcessAsync();
plc.Target("plc1").AssertWritten("GVL.Setpoint", 23.5f);
```

`plc.Pool` is an ordinary `IAdsConnectionPool` — inject it exactly as the application would.
`WithTarget(plcId)` (no seed) adds an empty target; `ConfigureTarget(plcId, configure)` reaches
`PlcTargetOptions` directly for anything else — timeouts, display name. It is a separate method
rather than a second `WithTarget` overload, since two overloads differing only in delegate type
would leave `WithTarget("plc1", x => …)` depending on how the compiler resolves an
implicitly-typed lambda between them. `Mode` stays forced to `Simulated` regardless of what
`ConfigureTarget` sets — a `TestPlc` never touches hardware.

## Seed, write, and what got recorded

Three verbs that look similar and are not:

| | Fires subscriptions | Recorded in `Writes` |
|---|---|---|
| `Target(id).Seed(path, value)` | no | no |
| `Target(id).Write(path, value)` | yes | **no** |
| a write by the code under test | yes | yes |

`Seed` is fixture setup. `Write` **drives** the PLC side — an input changing, a sensor moving —
so it fires subscriptions, or a system under test that subscribes would never react.

**A harness `Write` is deliberately excluded from the log.** The log answers "what did the code
under test write", and a harness write is not that. Without the exclusion, a test that primes
`GVL.Setpoint` and then asserts the code under test wrote it would pass while testing nothing.

## Assertions

```csharp
var target = plc.Target("plc1");

target.AssertWritten("GVL.Setpoint");                 // written at all
target.AssertWritten("GVL.Setpoint", 23.5f);          // written with this value
target.AssertNotWritten("GVL.Estop");
target.AssertWriteCount("GVL.Setpoint", 2);

target.Writes;                     // every recorded write, oldest first
target.WritesTo("GVL.Setpoint");   // just this path
target.ClearWrites();
```

Failures throw `PlcAssertionException` listing every write actually recorded for that path,
**with CLR types**:

```
Expected a write of 23.5 (Single) to "GVL.Setpoint" on plc1, but 2 write(s) were recorded:
  [0] 23.5 (Double)
  [1] 24 (Double)
```

The types are there because comparison is `Equals`-based and therefore type-sensitive — a boxed
`float` 23.5 does not equal a boxed `double` 23.5. That is the same rule the simulated connection
uses to decide whether a write is a change, and it is the likeliest reason a correct-looking
assertion fails.

## Reaching further

`Target(id).Simulated` is the live `SimulatedAdsConnection` — use it for enum metadata, ADS state,
or to observe *every* write including the harness's own via `ValueWritten`. For a connection that
must **fail** in a specific way, `TestPlc` is the wrong tool: see
[Hand-written doubles — `AdsConnectionBase`](#hand-written-doubles--adsconnectionbase).

## Hand-written doubles — `AdsConnectionBase`

`SimulatedAdsConnection` is the right first answer for a test: it is a working connection with a
real value store, real subscriptions and real RPC seeding. What it deliberately does not do is
**fail** — a specific `AdsErrorCode`, a timeout on the third call, a symbol that disappears
mid-run — and that is where a hand-written double comes from.

`IAdsConnection` has over two dozen members, so writing one by hand used to mean a screenful of
throwing stubs around the two that matter. Derive from `AdsConnectionBase` instead and override
only what the code under test reaches:

```csharp
// A working simulated connection that times out on the third read and nowhere else.
private sealed class FlakyConnection(IAdsConnection inner) : AdsConnectionBase
{
    private int _reads;

    public override string PlcId => inner.PlcId;

    public override Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct = default)
        => Interlocked.Increment(ref _reads) == 3
            ? throw new AdsErrorException("no answer", AdsErrorCode.ClientSyncTimeOut)
            : inner.ReadValueAsync<T>(symbolPath, ct);
}
```

Every member you do not override throws `NotSupportedException` naming your type and the member —
`FlakyConnection.SearchSymbolsAsync is not implemented` — rather than answering with a plausible
`null` or an empty list that would let the test pass while exercising a path you never specified.
Note that a double built this way forwards only what it declares: the example above delegates
typed reads and nothing else, so a service that also browses symbols needs that override too.

Three members have working defaults, because throwing there would cost overrides that have
nothing to do with what you are testing:

| Member | Default |
|---|---|
| `State` / `IsConnected` | Starts `Connected`. `SetConnectionState(…)` (protected) moves it and raises `ConnectionStateChanged` in one call, so a double can simulate an outage. |
| `ConnectionStateChanged` | Raised by `SetConnectionState` and nothing else. |
| `WithTimeout` | Validates the argument, then returns itself — a double does no I/O, so it has no bound to change. |

`PlcId` and `DisplayName` still throw: there is no honest default for an identity, and a double
reporting the wrong one is how a routing test passes while testing nothing. Both are a one-line
override.

```csharp
public sealed class OutageDouble : AdsConnectionBase
{
    public override string PlcId => "plc1";

    public void GoOffline() => SetConnectionState(ConnectionState.Disconnected);
}
```

Note this softens — but does not remove — the cost of adding a member to `IAdsConnection`: a
double deriving from the base keeps compiling and picks up a throwing default, while one
implementing the interface directly does not.
