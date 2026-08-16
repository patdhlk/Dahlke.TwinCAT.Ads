# PLC alarms

The optional **`Dahlke.TwinCAT.Ads.Alarms`** package tracks a TwinCAT alarm array — an
`ARRAY[..] OF ST_ErrorEntry` whose entries carry `sKey`, `Id`, `ErrorCode`, `ErrorType`,
`IsActive`, `NeedsAck`, `IsAcked` and a `PLCTimeStamp`. It keeps a live set of outstanding alarms
and streams `Raised` / `Acknowledged` / `Cleared` / `Reoccurred` / `Ended` transitions;
acknowledgement calls the PLC's own acknowledging method over ADS.

```bash
dotnet add package Dahlke.TwinCAT.Ads.Alarms
```

```json
{
  "PlcAlarms": {
    "TextCatalog": "alarms.json",
    "Targets": {
      "plc1": { "SymbolPath": "MAIN.ErrorHandler.aHmiAlarms", "CycleTimeMs": 200, "PlcClock": "Local" }
    }
  }
}
```

A target absent from `PlcAlarms:Targets` is simply not monitored — the package is opt-in per
target. Every misconfiguration (a `plcId` with no matching entry under `PlcTargets`, a missing
`SymbolPath`, a non-positive `CycleTimeMs`) is reported at startup, all at once. A relative
`TextCatalog` resolves against the host's content root, so `"alarms.json"` means the file next to
`appsettings.json` whatever the working directory happens to be. The full option list is in the
[configuration reference](configuration.md#plcalarms-section-optional-dahlketwincatadsalarms).

```csharp
builder.Services
    .AddTwinCatAds(builder.Configuration)
    .AddTwinCatAdsAlarms(builder.Configuration)
    .AddAlarmHandler<PagerNotifier>();

var app = builder.Build();
var monitor = app.Services.GetRequiredService<IPlcAlarmMonitor>();

await app.StartAsync();

// Only meaningful once the host is running. Before StartAsync nothing is subscribed, so
// GetOutstanding() is empty and AcknowledgeAsync finds no such alarm and returns false.
foreach (var alarm in monitor.GetOutstanding())
    Console.WriteLine($"{alarm.Key} ({alarm.Severity}): {alarm.Text}");

await monitor.AcknowledgeAsync("plc1", "BMK1Err404");

await app.WaitForShutdownAsync();

sealed class PagerNotifier(ILogger<PagerNotifier> log) : IPlcAlarmHandler
{
    public Task OnTransitionAsync(AlarmTransition t, CancellationToken ct)
    {
        log.LogWarning("{Kind}: {Key}", t.Kind, t.Alarm.Key);
        return Task.CompletedTask;
    }
}
```

## Handlers

**Register handlers with `AddAlarmHandler`, not `AlarmChanged`.** The monitor is a hosted service:
it registers its subscriptions during startup, and ADS delivers one notification per subscription
*as that subscription is registered* — so the whole outstanding set arrives inside `StartAsync`.
An `AlarmChanged` handler therefore has to be attached before the host starts, which DI cannot
express and which fails silently when you get it wrong: every alarm the PLC was already holding at
boot simply never reaches you. A handler registered with `AddAlarmHandler` is resolved as the
first step of the monitor's own startup, before any subscription exists, so being too late is not
a mistake it is possible to make. `AlarmChanged` remains for consumers who want the event and will
attach it before `StartAsync`.

`IPlcAlarmHandler.OnTransitionAsync` returns a `Task`, but that does not mean it returns
immediately: the monitor **awaits it** before publishing anything else, which is what buys
per-target ordering. The contract is the same as for `AlarmChanged` — be quick and hand the work
off. What the `Task` buys is that a handler with genuinely asynchronous work no longer has to
write `async void`, whose exceptions escape the monitor's isolation after the first `await`. The
`CancellationToken` is cancelled at shutdown; pass it along to anything slow. Handlers are
singletons — open a scope with `IServiceScopeFactory` if a transition needs scoped services — and
are isolated from one another exactly as `AlarmChanged` handlers are.

## The alarm model

**An alarm is outstanding while its fault is present OR it still awaits acknowledgement.** A PLC
alarm array is fixed-size with permanent slots — an alarm ends by `IsActive := FALSE`, never by
leaving the array — and when `NeedsAck` is set the entry outlives its fault condition until an
operator acknowledges it. So a fault that self-clears before anyone looks still reaches you,
`Cleared` marks the fault ending while the alarm stays outstanding, and `Ended` fires only once
the alarm is genuinely finished.

**Identity is `sKey`, not `Id`.** `Id` names the equipment (BMK) and is shared by every alarm on
that machine; `sKey` is the PLC's own composite key combining the equipment identifier and the
error code — `Test_Err_60` for equipment `Test`, error code `60`, for example. The exact spelling
is the PLC program's business and this package treats it as opaque: never parse it. Group by
`EquipmentId`, key by `Key`.

## Acknowledgement

**Acknowledgement is a method call on the PLC, not a write to the entry.** `AcknowledgeAsync`
finds the alarm by `Key` among the outstanding set, then calls the PLC method named by
`AcknowledgeMethod` (default `AcknowledgeAlarm`) on the function block that owns acknowledgement,
passing that key. The block's instance path is derived by trimming the last segment off
`SymbolPath` — `MAIN.ErrorHandler.aHmiAlarms` derives `MAIN.ErrorHandler` — which is right when
the alarm array is a member of that block and wrong for every other layout; set
`AcknowledgeInstancePath` for those. The returned `deaReturnType` is resolved **by name**, not by
number: `SUCCESS` returns `true`, `NOT_FOUND` returns `false`, and any other member raises
`PlcAlarmAcknowledgeException` naming it. Nothing is written to the array entry — on hardware the
array is a read-only projection that accepts an `IsAcked` write and discards it, which is exactly
why acknowledgement goes through the block instead. Naming the alarm by key also means no slot is
addressed, so there is no window in which the acknowledgement could land on whatever alarm has
since taken that slot. A vendor whose PLC acknowledges differently registers its own
`IPlcAlarmDialect`; the shipped one is simply the default. Registering one before
`AddTwinCatAdsAlarms` also takes the shipped dialect's configuration rules out of startup
validation, so a PLC that needs no instance path is no longer asked for one.

**The PLC method needs `{attribute 'TcRpcEnable'}`.** A method without it is not reachable over
ADS at all, and the call fails as an unknown method however correct the instance path and the name
are. When acknowledgement raises `AdsErrorException` on a target whose alarms otherwise arrive
normally, check the attribute on the method declaration first — it is the likeliest cause and the
cheapest to rule out.

## Restarts, offline targets and ordering

**Every restart re-raises everything outstanding.** The outstanding set lives in memory and starts
empty, and ADS delivers one notification on registration — so the first snapshot after a host
restart reports every alarm the PLC is currently holding as newly `Raised`. There is no way to
tell a two-day-old alarm from one raised while the host was down without persisting the set.
Forwarding `Raised` straight to a pager therefore pages the whole outstanding set on every
deployment: deduplicate downstream on `Key` plus `PlcTimestamp`, or suppress the first snapshot
after start.

**A PLC that is down at boot does not fail the host.** Its first subscription attempt cannot
succeed, so it is logged at `Error` and retried automatically the next time that target's
connection reports `Connected` — every other target monitors normally in the meantime, and the
offline one simply reports no alarms until it comes back. Once a target is registered the core
library's durable subscriptions take over and carry it across later reconnects. Registration is
attempted for all targets concurrently, so ten offline PLCs cost one connection timeout, not ten.
Only unreachability is treated this way: a `SymbolPath` the PLC does not have is a configuration
fault that no reconnect will fix, and still fails startup — **provided that target answered at
boot**. If it did not, there is no startup left to fail: the bad path surfaces on the deferred
retry instead, where it is logged at `Error` and re-attempted on every reconnect, forever. Watch
the log for a target that never reports `Monitoring alarms on …`.

**Transitions for one target arrive in the order they were computed**, so a consumer folding the
stream into its own state never sees a `Raised` land after the `Ended` that followed it. That
ordering is bought by holding the target's lock across delivery, which means **a handler that
blocks delays that target's next snapshot** — do the minimum on the notification thread and hand
the work off. Ordering is per target, not across targets.

`IPlcAlarmHandler`/`AlarmChanged` and `Transitions` treat a throwing consumer differently, on
purpose. Registered handlers and `AlarmChanged` handlers are isolated from one another: one that
throws is logged and the rest still receive that transition. `Transitions` is an ordinary
`IObservable<AlarmTransition>` and follows the standard Rx contract — throwing from `OnNext` is an
observer bug, and a subscriber that throws can starve the subscribers after it for that
transition. Either way the exception never escapes onto the notification thread, and the next
transition is still delivered.

## Fault tolerance

**A shape mismatch is loud, but not fatal.** If the PLC's `ST_ErrorEntry` stops matching what the
package binds, it throws `PlcAlarmShapeException` naming the member and the symbol path rather
than degrading to defaults — a silently wrong alarm list is worse than an absent one. That
snapshot is dropped whole and logged at `Error`, the outstanding set keeps its last good reading,
and the subscription survives to recover on the next well-formed notification. The report is
**once per outage, not once per notification** — a mismatch is a property of the PLC's type and
would otherwise repeat at cycle rate for as long as the fault lasts. Further failures are counted
silently; when a notification binds again the monitor says so at `Information` and names how many
failed in between, and a fault that recurs after a recovery is reported in full again.

**A bad *value* is not a shape mismatch, and stays in its own slot.** That distinction is the
difference between dropping one field and going blind: the alarm array is fixed-size with
permanent slots, so an entry carrying nonsense arrives again on every notification, and treating
it as a broken type would drop every snapshot for as long as it sat there. Two values are
therefore kept rather than thrown on — an `ErrorType` outside `E_ErrorType`, which binds with its
raw number, and a `PLCTimeStamp` that is not a real date, which reads as `default(DateTime)`
exactly as a zeroed `TIMESTRUCT` already does. Each is logged once at `Warning` (per distinct
value, and per slot, respectively) instead of on every cycle, and every other entry in the array
binds normally. A *missing* or *retyped* member is still a shape mismatch and still behaves as
above.

## Health checks

```csharp
builder.Services
    .AddHealthChecks()
    .AddTwinCatAdsHealthCheck()        // can each target be reached at all?
    .AddTwinCatAdsAlarmHealthCheck();  // how bad are the alarms it can see?
```

`AddTwinCatAdsAlarmHealthCheck()` reports Degraded at `Warning` and Unhealthy at `Error` by
default, from the worst severity outstanding. **It reports only that** — it says nothing about
whether monitoring is live, so a target still waiting for its first connection has no alarms and
looks healthy here, indistinguishable from one that is connected and genuinely quiet. Register the
core's `AddTwinCatAdsHealthCheck()` alongside it for per-target connectivity; the two together are
the full picture.

See
[`examples/Dahlke.TwinCAT.Ads.Examples.ErrorHandler`](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/tree/main/examples/Dahlke.TwinCAT.Ads.Examples.ErrorHandler)
for a runnable demo that walks a whole alarm lifecycle in simulation.
