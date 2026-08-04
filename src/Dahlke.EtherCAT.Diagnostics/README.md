# Dahlke.EtherCAT.Diagnostics

EtherCAT master and slave diagnostics over TwinCAT ADS: topology, slave and port state, CRC and frame error counters, sync-unit faults, CoE object reads, and a change-event stream.

It reads through **raw ADS index groups** rather than the symbol API, because that is the only way to reach an EtherCAT master — there are no PLC symbols for any of this. The raw channel comes from [Dahlke.TwinCAT.Ads](https://www.nuget.org/packages/Dahlke.TwinCAT.Ads), so you get its connection pooling, reconnection and simulation for free.

```bash
dotnet add package Dahlke.EtherCAT.Diagnostics
```

## Quick start

```csharp
builder.Services.AddTwinCatAds(builder.Configuration);      // the raw channel factory
builder.Services.AddEtherCatDiagnostics();                  // client, cache, polling monitor
```

You must also supply two application concerns this library deliberately does not invent:

```csharp
builder.Services.AddSingleton<IEtherCatOptionsSource, MyOptionsSource>();      // which masters to poll
builder.Services.AddSingleton<IEtherCatDiagnosticsHandler, MyHandler>();       // where events go
```

Then read the bus:

```csharp
public sealed class TopologyEndpoint(IEtherCatClient client)
{
    public async Task<string> DescribeAsync(CancellationToken ct)
    {
        var masters = await client.GetMastersAsync("192.168.1.10.1.1", ct);
        var master  = masters[0];

        var state  = await client.GetMasterStateAsync(master.AmsNetId, ct);
        var slaves = await client.GetConfiguredSlavesAsync(master.AmsNetId, ct);
        var frames = await client.GetFrameStatisticsAsync(master.AmsNetId, ct);

        return $"{master.Name}: {state?.State}, {slaves?.Count} slaves, {frames?.LostFrames} lost";
    }
}
```

## What it gives you

| | |
|---|---|
| `IEtherCatClient` | One-shot reads: masters, master state, configured vs. **scanned** slaves, per-slave detail, error counters, sync units, CoE objects. Counters are resettable. |
| `IEtherCatCache` | The last snapshot the monitor took, so a request path never has to touch the bus. |
| `IEtherCatMonitor` | The polling loop, registered as a hosted service. Re-arm a CRC notification with `ClearCrcNotification`. |
| `IEtherCatEvent` | The change stream — slave present/absent, slave and master state changes, CRC threshold exceeded, sync-unit fault, diagnostics degraded. |

**Configured vs. scanned matters.** `GetConfiguredSlavesAsync` returns what the project says should be on the bus; `GetScannedSlavesAsync` returns what is actually answering. The difference is the diagnosis.

**Nullable returns are the contract, not an oversight.** A master that is not reachable yields `null` rather than throwing, so a dashboard polling six masters is not taken down by one unplugged cable.

## Turning the polling off

`AddEtherCatDiagnostics(startMonitor: false)` registers everything but does not run the loop. There is no internal enable flag, and this is why: a REST controller is activated by its framework *before* whatever feature gate would have turned the request away, so its dependencies have to resolve either way. Registering without polling lets the surface exist while the bus is left alone. Not calling `AddEtherCatDiagnostics` at all is the other way to turn it off, and the right one when nothing references the library.

## Testing without hardware

Because the transport is `Dahlke.TwinCAT.Ads`'s raw channel, its simulation applies — seed raw index-group responses and exercise this library with no PLC and no TwinCAT installation. See the raw-channel simulation section of the [repository README](https://github.com/patdhlk/Dahlke.TwinCAT.Ads#simulation).

## Links

- Source, issues and the other packages in this repository: <https://github.com/patdhlk/Dahlke.TwinCAT.Ads>
- Changelog: <https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/CHANGELOG.md>

Apache-2.0.
