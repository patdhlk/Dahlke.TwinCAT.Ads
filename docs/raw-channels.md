# Raw ADS channels

For targets the symbol API cannot reach — EtherCAT masters and slaves, the TwinCAT system
service — inject `IAdsRawChannelFactory` and address them by index group and index offset.

```csharp
public sealed class EtherCatDiagnostics(IAdsRawChannelFactory channels)
{
    public async Task<ushort> ReadSlaveStateAsync(
        string masterNetId, ushort slaveAddress, CancellationToken ct)
    {
        var channel = channels.Get(masterNetId, 0xFFFF);

        var buffer = new byte[2];
        await channel.ReadAsync(0x0009, slaveAddress, buffer, ct);
        return BitConverter.ToUInt16(buffer);
    }
}
```

Channels are cached per `(amsNetId, port)` and **never disposed by you** — hold the reference as
long as you like. `Get` never blocks and never throws for a present Net ID, however malformed; an
unreachable target simply reports `Disconnected` until you operate on it. Only a `null` Net ID
throws (`ArgumentNullException`), because that is a caller bug rather than a target that happens
not to exist.

The Net ID is trimmed and canonicalised before it is used as a key, so `"1.2.3.4.5.6"`,
`"01.2.3.4.5.6"` and `" 1.2.3.4.5.6"` are one channel. An octet outside 0–255 is **zeroed rather
than rejected** — `Get("999.1.1.1.1.1", 851)` addresses `0.1.1.1.1.1`, and a warning is logged
once per distinct spelling — because that is how the ADS stack resolves the address on the wire.
Configured Net IDs — targets, routes, the router's own and seed entries — are stricter and reject
the same value at startup; see [Simulation](#simulation).

Raw channels are unrelated to `PlcTargets`: they need no configured target, and `RawChannels:Seed`
names targets only to pre-load their simulated contents, never to declare which ones are
reachable. Because a real raw channel cannot route without the embedded AMS router, leaving
`RawChannels.Mode` at its default `Real` starts the router even when every configured PLC target
is simulated. `AddTwinCatAdsSimulation` forces `RawChannels.Mode` to `Simulated` so it never does.

The [`Dahlke.EtherCAT.Diagnostics`](https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/src/Dahlke.EtherCAT.Diagnostics/README.md)
package is built entirely on raw channels — if what you want is EtherCAT topology, error counters
or CoE access, start there rather than here.

## What to expect when things go wrong

| Situation | Result |
|---|---|
| Device answers with an error code | `AdsErrorException` — an answer, never retried |
| Every attempt timed out | `TimeoutException` |
| You cancelled | `OperationCanceledException` carrying your token; no further attempt is made |
| Transport could not be opened, or the host has shut down | `AdsConnectionUnavailableException` |

The timeout bounds **each attempt**, so the default 5000 ms with one retry can take 10 seconds
before throwing — the worst case for the attempts is `TimeoutMs × (RetryCount + 1)`. Pass an
explicit `TimeSpan` overload when you need a tighter bound: probing a slave that may have no
mailbox, for instance. A call that also has to build the transport for a channel with live
subscriptions waits for that rebuild's restore pass first, which re-registers those subscriptions
one at a time under their own separate bounds; that wait is not contained by the call's own
attempt bound.

The table covers the cases the contract names, not every exception the runtime can produce.

## Notifications

```csharp
var handle = await channel.SubscribeAsync(
    indexGroup: 0x0009, indexOffset: slaveAddress, length: 2, cycleTimeMs: 200,
    handler: data => Console.WriteLine(BitConverter.ToUInt16(data)),
    ct);
```

The handler receives a `ReadOnlySpan<byte>` valid only for that call — copy with `data.ToArray()`
if you need to keep it. The compiler will not let you store the span itself, which is the point;
the cost is that a handler cannot be `async`. Against a real target the handler runs on the ADS
notification thread, never the caller's, so it must be thread-safe and must not block. A handler
that throws is logged at Warning and keeps its subscription.

Registration is bounded by `TimeoutMs` and is deliberately **never retried** — a retry would mean
dropping and rebuilding the transport, re-registering every *other* subscription on the channel as
a side effect of one subscriber's retry.

Subscriptions survive a transport drop and are re-registered automatically, exactly once, while
your handle stays valid. A live subscription pins its channel against idle eviction, so **dispose
the handle** when you are done — dropping it on the floor holds a connection open for the
factory's lifetime. After host shutdown no transport is rebuilt, so a live subscription simply
goes quiet rather than raising anything.

## Simulation

Raw-channel options bind from the `RawChannels` section, alongside `PlcTargets` and `AmsRouter` —
the full option tables are in the
[configuration reference](configuration.md#rawchannels-section):

```jsonc
{
  "RawChannels": {
    "Mode": "Simulated",
    "Seed": [
      {
        "AmsNetId": "192.168.1.10.3.1",
        "Port": 65535,
        "Slots": [
          { "IndexGroup": "0x11", "IndexOffset": 1001, "Bytes": "02000000410C0000" }
        ]
      }
    ]
  }
}
```

`Seed` is an **array of objects** rather than a dictionary keyed on `amsNetId:port`, because `:`
is the configuration hierarchy separator — such a key flattens into nested sections and loses
everything after the Net ID.

The same options are settable in code, which is what a host with no configuration file wants:

```csharp
builder.Services.AddTwinCatAds(o =>
{
    o.RawChannels.Mode = ConnectionMode.Simulated;
    o.RawChannels.Seed.Add(new AdsRawChannelSeed
    {
        AmsNetId = "192.168.1.10.3.1",
        Port = 65535,
        Slots = [new AdsRawChannelSeedSlot
        {
            IndexGroup = "0x11", IndexOffset = "1001", Bytes = "02000000410C0000",
        }],
    });
});
```

`IndexGroup` and `IndexOffset` accept decimal or `0x`-prefixed hex, which is why they are strings.
A seed entry's Net ID is matched against the channel **after normalisation**, so `01.2.3.4.5.6`
still seeds the channel for `1.2.3.4.5.6`.

> **Note.** `AddTwinCatAdsSimulation` forces `Mode` to `Simulated` *after* binding, so a host that
> sets `"Mode": "Real"` in configuration and calls that helper still gets simulation.

Seeding is also available at runtime, which is what tests and demo hosts usually want:

```csharp
if (channels.TryGetSimulated("192.168.1.10.3.1", 0xFFFF, out var sim))
    sim.Seed(0x11, 1001, [0x02, 0x00, 0x00, 0x00]);
```

The store knows nothing about EtherCAT, CoE or files — seed the bytes your own decoder expects. An
unseeded read answers `DeviceInvalidOffset`, the same code real hardware gives, so your error
handling is exercised too. `ReadWriteAsync` in simulation writes the source to the slot and
returns the slot's bytes; it will never invent a file handle, so seed the response you expect.

Simulated subscriptions ignore `cycleTimeMs` and fire on every write made *through the channel* to
the watched slot, with no coalescing, on whichever thread performed the write. `Seed` writes the
slot **without** firing — it arranges state rather than reporting a change.

Seed entries are validated at startup in **both** modes, so a malformed entry left behind after
switching to `Real` still fails the host instead of sitting silently broken. A seed entry's AMS
Net ID must be six dot-separated octets each in 0–255 — stricter than `Get`, deliberately, because
a declaration's typo has no correct reading.
