# Dahlke.EtherCAT.Esi

An EtherCAT Slave Information (ESI) device catalogue. It parses vendor ESI XML and answers one question: given a vendor / product / revision triple read off a live bus, which device description is this?

**No ADS, no TwinCAT, no Beckhoff dependency.** This package is XML, options and logging. That independence is deliberate — it is why the catalogue is separate from [Dahlke.EtherCAT.Diagnostics](https://www.nuget.org/packages/Dahlke.EtherCAT.Diagnostics), which is the package that talks to a master over ADS. Use this one alone if all you have is a folder of ESI files.

```bash
dotnet add package Dahlke.EtherCAT.Esi
```

## Quick start

Point it at a directory of ESI XML and resolve an identity:

```csharp
builder.Services.AddEsiCatalog(builder.Configuration.GetSection("Esi"));
```

```jsonc
{
  "Esi": {
    "Directory": "C:/TwinCAT/3.1/Config/Io/EtherCAT",
    "LookupBudgetMs": 5000
  }
}
```

```csharp
public sealed class SlaveNamer(IEsiCatalog catalog)
{
    public async Task<string> DescribeAsync(uint vendorId, uint productCode, uint revision)
    {
        var result = await catalog.LookupAsync(
            new EsiKey(vendorId, productCode, revision),
            typeHint: "EL3204");

        return result.Status switch
        {
            EsiStatus.Resolved => result.Device!.NameEn ?? "unnamed device",
            EsiStatus.NotFound => "unknown device",
            _                  => $"lookup failed: {result.Status}",
        };
    }
}
```

`EsiStatus` separates a resolved device from one that could not be resolved *and why*: `NotConfigured` when no ESI directory is set or it does not exist, `NotFound` when nothing matched, and further members for an identity that was never scanned or a directory that could not be read. So a caller can tell "I have no ESI folder configured" from "I looked and it is not there".

One case deliberately does **not** get its own status: a lookup that exhausts `LookupBudgetMs` reports `NotFound`, the same as genuine absence. It is logged at warning with the budget that was hit, so it is diagnosable — but if your code needs to distinguish "not on the bus" from "I stopped looking", watch the log rather than the status.

## What it does for you

**Ranks candidates by the type hint.** A real ESI folder holds hundreds of files and the sought identity is in one of them. The hint (typically the slave's type string, e.g. `EL3204`) orders the search so the likely file is opened first, rather than parsing the folder alphabetically.

**Bounds the work.** `LookupBudgetMs` caps a single lookup. The budget is checked *between files*, not once up front, so a large folder cannot be turned into an unbounded scan by one unlucky query — the lookup gives up and warns rather than blocking a request thread.

**Parses each device at most once per process, and complains at most once per device.** Both are properties of a single shared instance, which is why `AddEsiCatalog` registers `IEsiCatalog` as a singleton. Any other lifetime silently loses them.

**Tolerates a bad folder.** A malformed or unreadable ESI file does not fail the lookup that happened to reach it; it is logged once and skipped, so one corrupt vendor file cannot take out the catalogue.

## Registration is not eager

`AddEsiCatalog` does not resolve the catalogue. Whether a misconfigured ESI directory should be reported at startup or on first use is a hosting decision, so it is left to you:

```csharp
var app = builder.Build();
app.Services.GetRequiredService<IEsiCatalog>();   // fail at startup instead
```

## Links

- Source, issues and the other packages in this repository: <https://github.com/patdhlk/Dahlke.TwinCAT.Ads>
- Changelog: <https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/CHANGELOG.md>

Apache-2.0.
