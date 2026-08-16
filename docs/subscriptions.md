# Subscriptions

Durable value-change notifications, with an optional Rx companion package that surfaces them as
`IObservable<T>` streams.

## Typed subscription (preferred)

```csharp
using var sub = await conn.SubscribeAsync<float>(
    "GVL.Temp",
    cycleTimeMs: 200,
    (symbol, value) => Console.WriteLine($"{symbol} = {value}"),
    CancellationToken.None);

// Subscription survives reconnects; dispose to remove permanently.
```

## Untyped subscription

```csharp
using var sub = await conn.SubscribeAsync(
    "GVL.Counter",
    cycleTimeMs: 500,
    (symbol, value) => Console.WriteLine($"{symbol} = {value}"),
    CancellationToken.None);
```

Subscriptions are durable: owned by the stable facade, not the underlying connection. When a
reconnect occurs the subscription is automatically re-registered against the new connection.
Callbacks fire on a background thread — they must be thread-safe and must not block. A `null`
notification value with a value-type `T` is dropped (Warning logged). Dispose is idempotent and
thread-safe.

## Notification metadata

```csharp
using var sub = await conn.SubscribeAsync("GVL.Temp", cycleTimeMs: 200,
    n => Console.WriteLine($"[{n.Timestamp:O}] {n.SymbolPath} ({n.TypeName}) = {n.Value}"),
    CancellationToken.None);
```

Carries the same durability guarantees as the untyped overload, plus the symbol's PLC type name
and the PLC-reported timestamp of the change. Struct, function block and array notifications are
decoded off the notification thread and so may be delivered slightly later — and, under a fast
burst, out of order relative to scalar notifications.

## Reactive (Rx) companion

The optional **`Dahlke.TwinCAT.Ads.Reactive`** package exposes subscriptions and connection state
as `IObservable<T>` streams (built on `System.Reactive`). Install it alongside the core package
only if you want Rx — the core package never depends on `System.Reactive`.

```bash
dotnet add package Dahlke.TwinCAT.Ads.Reactive
```

```csharp
using Dahlke.TwinCAT.Ads.Reactive;
using System.Reactive.Linq;

var conn = pool.GetConnection("plc1");

// Typed value stream — cold: each Subscribe opens its own ADS notification,
// disposing it deletes the notification. Durable across reconnects.
using var sub = conn.ObserveValue<float>("GVL.Temp", cycleTimeMs: 200)
    .Select(change => change.Value)
    .Where(t => t > 50f)
    .DistinctUntilChanged()
    .Subscribe(t => Console.WriteLine($"Hot: {t} °C"));

// Connection state across every configured target (each event carries its PlcId).
using var states = pool.ObserveAllConnectionStates()
    .Subscribe(e => Console.WriteLine($"{e.PlcId}: {e.PreviousState} -> {e.State}"));
```

Each notification is an `AdsValueChange<T>` record (`Symbol`, `Value`). Notifications arrive on a
background thread — add `.ObserveOn(...)` before updating UI. To share one underlying ADS
notification among multiple subscribers, add `.Publish().RefCount()`. See
[`examples/Dahlke.TwinCAT.Ads.Examples.Reactive`](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/tree/main/examples/Dahlke.TwinCAT.Ads.Examples.Reactive)
for a runnable demo.
