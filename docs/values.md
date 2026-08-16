# Reading and writing values

Typed reads and writes, PLC struct binding, typed symbol handles, batch operations, symbol
browsing, and the IEC 61131-3 type mapping underneath them all.

## Typed reads (preferred)

```csharp
public class TempService(IAdsConnectionPool pool)
{
    public async Task<float> GetTemperatureAsync(CancellationToken ct)
    {
        var conn = pool.GetConnection("plc1");
        return await conn.ReadValueAsync<float>("GVL.Temp", ct);
    }
}
```

Supported conversions: widening numeric casts (e.g. PLC `INT` stored as `int` readable as
`double`), and string-seeded simulation values via `Convert.ChangeType` with
`CultureInfo.InvariantCulture` (e.g. `"42"` → `int`, `"true"` → `bool`).

## Reading a PLC struct into a .NET type

A struct, function block or union decodes to a member tree, and that tree binds onto a .NET type
by member name:

```csharp
public record MotorState(int Speed, bool Running);

// From a metadata or batch read — the tree a real connection already produces:
var result = await conn.ReadValueWithMetadataAsync("MAIN.Motor");
MotorState motor = result.GetValue<MotorState>();

// Or on a simulated target, straight through the typed read:
MotorState simulated = await conn.ReadValueAsync<MotorState>("MAIN.Motor");
```

Positional records, mutable classes, structs, nested structs and arrays all bind; member names
match case-insensitively, and each member gets the same widening a scalar read would.

**Every member of the target type must be present in the tree.** A member the PLC does not supply
fails with a message naming it, rather than being left at its default — a type that disagrees with
the PLC is what you want to hear about. Extra members in the PLC's struct are ignored, so the
target type drives; to read a subset, read the symbol as `IReadOnlyDictionary<string, object?>`
instead.

> **What simulation can and cannot prove here.** Binding matches by **name**, because a decoded
> tree has no memory layout. `ReadValueAsync<T>` against real hardware hands `T` to Beckhoff's
> marshaller, which maps PLC memory by **declaration order**. So a simulated target catches a
> misspelled or mistyped member — and cannot catch a mis-ordered one. Verify field order against
> hardware.

## Dynamic (untyped) reads

```csharp
object? value = await conn.ReadValueAsync("GVL.Counter", ct);
```

Use the untyped overload when the target type is not known at compile time (generic dashboards,
reflection-driven serialisation).

## Typed writes

```csharp
await conn.WriteValueAsync<float>("GVL.Setpoint", 23.5f, ct);
// Or let the compiler infer T:
await conn.WriteValueAsync("GVL.Counter", (short)42, ct);
```

## The cancellation token is optional

Every async member defaults its `CancellationToken`, so code with no token to pass writes nothing
rather than `CancellationToken.None`:

```csharp
float temp = await conn.ReadValueAsync<float>("GVL.Temp");
await conn.WriteValueAsync("GVL.Setpoint", 23.5f);
```

Pass one wherever you have one — the semantics are unchanged, and an omitted token means `None`.

## Typed symbol handles

A path written as a string literal and a type written as a type argument are both unchecked, and
both are repeated at every call site that touches the symbol. `PlcSymbol<T>` declares them once:

```csharp
static class Symbols
{
    public static readonly PlcSymbol<float> Setpoint = new("GVL.Setpoint");
    public static readonly PlcSymbol<bool>  Pump     = new("GVL.PumpRunning");
}

float setpoint = await conn.ReadValueAsync(Symbols.Setpoint);   // T inferred from the handle
await conn.WriteValueAsync(Symbols.Pump, true);                 // bool checked at compile time
using var sub = await conn.SubscribeAsync(Symbols.Setpoint, 200, (_, v) => Console.WriteLine(v));

// Batch results are keyed by path and untyped; read one back through its handle:
var results = await conn.ReadValuesAsync([Symbols.Setpoint.Path, Symbols.Pump.Path]);
float fromBatch = results.GetValue(Symbols.Setpoint);
```

Handles are plain values — equal by path and type, free to build, holding no connection and
registering nothing. They are extension methods over `IAdsConnection`, so they work against any
implementation (including your own test doubles) and compose with
[`WithTimeout`](connections.md#per-call-timeouts--withtimeout).

**This cannot check a path against the PLC** — nothing on this side of the wire can, so
`DeviceSymbolNotFound` and `InvalidCastException` remain possible. What changes is that there is
exactly one place per symbol to correct when they happen. The Rx companion has matching
`ObserveValue` overloads.

## Batch operations

```csharp
// Batch write: IReadOnlyDictionary<string, object?> input
await conn.WriteValuesAsync(new Dictionary<string, object?>
{
    ["GVL.Setpoint"] = 21.5f,
    ["GVL.PumpRunning"] = true,
}, ct);

// Batch read: IReadOnlyDictionary<string, AdsValueResult> result
var results = await conn.ReadValuesAsync(["GVL.Setpoint", "GVL.PumpRunning"], ct);

foreach (var (symbol, result) in results)
{
    if (result.Succeeded)
        Console.WriteLine($"{symbol} = {result.Value}");
    else
        Console.WriteLine($"{symbol} FAILED: {result.Error!.Message}");
}

// Typed access on a result:
float setpoint = results["GVL.Setpoint"].GetValue<float>();
```

On real connections a batch write is a single ADS sum command. A batch **read** partitions by
category: scalars, strings and enums share one sum command, while structs, function blocks,
unions and arrays are decoded individually so their members come back as a tree rather than an
opaque value. So an all-scalar batch costs one round-trip, and a batch containing containers costs
one plus the container decodes. Every result carries `TypeName` and `Category` either way.

A per-symbol failure is captured in `AdsValueResult.Error` and does not abort the batch. A
whole-batch timeout throws `TimeoutException`; caller cancellation throws
`OperationCanceledException`.

## PLC method calls and enum metadata

`InvokeRpcMethodAsync` calls a method on a function-block instance (in and out parameters, and the
return value); `GetEnumMembersAsync` reads a PLC enumeration's members from the running program,
so a returned code can be read by name instead of by number. The PLC method must carry
`{attribute 'TcRpcEnable'}` to be reachable over ADS at all — see
[troubleshooting](troubleshooting.md#an-rpc-call-fails-as-an-unknown-method).

## Symbol browsing and metadata

```csharp
// One level — what interactive drill-down wants. Call again per level as the user expands.
var roots = await conn.GetSymbolsAsync(null, includeChildren: false);
foreach (var s in roots)
    Console.WriteLine($"{s.InstancePath} : {s.TypeName} ({s.Category}, {s.ByteSize}B)");

// The entire subtree, in one call. With a null parent that is EVERY symbol on the PLC.
var tree = await conn.GetSymbolTreeAsync("MAIN");

// Search across the whole tree by substring
var motors = await conn.SearchSymbolsAsync("Motor", includeChildren: false);

// Read a value together with its PLC type
var result = await conn.ReadValueWithMetadataAsync("MAIN.Motor");
Console.WriteLine($"{result.TypeName} = {result.Value}");   // ST_Motor = Dictionary<string, object?>
```

**Pick the shape deliberately.** `GetSymbolTreeAsync` populates `Children` recursively all the way
down, so a root call projects every symbol on the PLC — tens of thousands of `AdsSymbolInfo`
objects on a large program. `GetSymbolsAsync(parentPath, includeChildren: false)` returns one
level with `Children` null, which is the cheap shape.

> **Deprecated:** `GetSymbolsAsync(parentPath)` — the two-argument overload — has always meant
> `includeChildren: true`, so the terse call was the expensive one while the cheap browse needed
> the longer signature. It still behaves exactly as before and now warns. Replace it with
> `GetSymbolTreeAsync(parentPath)` for the same result, or
> `GetSymbolsAsync(parentPath, includeChildren: false)` if one level is what you meant. It is
> being retired rather than repurposed: flipping it to mean one level would have left every call
> site compiling while silently changing what it did, so **no future version will reintroduce
> that shape as a one-level browse**.

Browsing is bounded by `PlcTargetOptions.SymbolBrowseTimeoutMs` (default 30 s) rather than
`TimeoutMs`, since uploading the symbol table can take longer than a typical read or write. That
timeout bounds how long the *caller* waits — the underlying upload is a blocking Beckhoff call and
continues in the background if abandoned.

`ReadValueWithMetadataAsync` decodes structs, function blocks and unions to
`Dictionary<string, object?>` keyed by member name, arrays to `object?[]`, and passes scalars
through unchanged. `ALIAS`, `PROGRAM`, `POINTER` and `REFERENCE` categories are currently passed
through as-is, so a value of one of those types may surface as a raw TwinCAT object rather than a
neutral tree.

## IEC 61131-3 type mapping

`Iec61131Converter` is a table-driven utility that maps IEC 61131-3 elementary type names to and
from .NET types, supplies typed default values, and converts boxed values — reusing the same
invariant-culture conversion core as typed reads. It exposes two tiers:

- **`Iec61131Converter` (strict core)** — recognises only the canonical uppercase IEC names (`BOOL`, `DINT`, `LREAL`, …), matched case-sensitively. Use this when you require strict, standard names.
- **`Iec61131Converter.Beckhoff` (lenient tier)** — case-insensitive and alias-aware. It recognises mixed-case names and Beckhoff/non-standard aliases (`dtSystemTime` → `DT`, `T_UD` → `TIME`, `BIT`/`BIT8` → `BOOL`), normalises them to a canonical name, then delegates to the strict core.

```csharp
// Forward: IEC name -> .NET Type (strict, case-sensitive)
Type t = Iec61131Converter.GetDotNetType("DINT");        // typeof(int)

// Reverse: .NET Type -> canonical IEC name (deterministic)
string n = Iec61131Converter.GetIecTypeName(typeof(int)); // "DINT"

// Default value and conversion (invariant culture)
object? d = Iec61131Converter.GetDefaultValue("STRING");   // "" (never null)
object? v = Iec61131Converter.ConvertValue("LREAL", "3.14"); // 3.14 (double)

// Lenient tier: case-insensitive + Beckhoff aliases
Type b = Iec61131Converter.Beckhoff.GetDotNetType("dint");        // typeof(int)
Type s = Iec61131Converter.Beckhoff.GetDotNetType("dtSystemTime"); // typeof(DateTime)
```

The forward map is many-to-one: the bit-string types and unsigned-integer types share a .NET type
(`BYTE` and `USINT` both → `byte`; `STRING` and `WSTRING` both → `string`). The reverse map is
deterministic — an unsigned .NET integer resolves to the unsigned-integer IEC type (`byte` →
`USINT`, never `BYTE`), and `string` resolves to `STRING`.
