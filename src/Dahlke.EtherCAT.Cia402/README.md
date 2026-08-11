# Dahlke.EtherCAT.Cia402

CiA-402 (DS402) drive profile decoders. Turns a statusword, controlword or modes-of-operation byte into the state, command and mode it stands for — so you stop decoding `0x0637` by hand.

**No dependencies at all.** Not ADS, not TwinCAT, not EtherCAT, and not `Microsoft.Extensions.*` either: this package ships with an empty dependency list on every target framework. It is pure functions over integers, so it works the same whether the word came from EtherCAT CoE, SoE, a CANopen gateway, a serial link, or a log file from last week.

```bash
dotnet add package Dahlke.EtherCAT.Cia402
```

## Quick start

```csharp
using Dahlke.EtherCAT.Cia402;

var status = Cia402.DecodeStatusword(0x0637);

status.State;          // Cia402State.OperationEnabled
status.TargetReached;  // true
status.Remote;         // true — clear this and the drive is in local control

Cia402.DescribeStatusword(0x0637);
// "OperationEnabled: voltage enabled, remote, target reached"
```

The other two objects:

```csharp
Cia402.DescribeControlword(0x010F);      // "EnableOperation: halt"
Cia402.DescribeModeOfOperation(8);       // "Cyclic Synchronous Position (csp)"

Cia402.EncodeCommand(Cia402Command.Shutdown);         // 0x0006
Cia402.EncodeCommand(Cia402Command.SwitchOn);         // 0x0007
Cia402.EncodeCommand(Cia402Command.EnableOperation);  // 0x000F
```

Those last three are the enable sequence, in order. `EncodeCommand` is the exact inverse of `DecodeControlword`, and a test pins the round trip for every command.

## The parts that are easy to get wrong

**Quick stop (statusword bit 5) is active low.** Set means *not* quick-stopping. Three of the eight state-table rows require it set, and reading it the intuitive way puts a healthy drive in `QuickStopActive`.

**Not every statusword names a state.** The CiA-402 state table does not cover all 65536 words, and real drives produce words outside it: `0x1591` — read off the drive this package was written for — has bit 0 set while quick stop is clear, which no row admits. Those decode to `Cia402State.Unknown`, and `DescribeStatusword` carries the raw word (`"Unknown(0x1591): voltage enabled, warning, target reached"`) so it is never a dead end. The flag bits are decoded either way; they are individual bits and do not depend on the table matching.

**"Switch on" and "Disable operation" are the same word.** The standard lists both as `0xxx0111` — transitions 3 and 5 — so which one a drive performs depends entirely on the state it is in when the word arrives. One word cannot tell them apart, so both decode to `Cia402Command.SwitchOn`. There is deliberately no second enum member for a distinction this type cannot make.

**Fault reset is an edge, not a level.** Controlword bit 7 resets on its *rising* edge, so holding `0x0080` clears one fault and then does nothing. Nothing here can see an edge — it decodes one word — so a word with bit 7 set reports `FaultReset` whatever the low bits spell, which is the precedence the standard's own table gives it.

**A negative mode of operation is the manufacturer's.** `DecodeModeOfOperation` returns `null` for anything CiA-402 does not define — the reserved `5`, `12` and up, and every negative value. Null rather than a `ManufacturerSpecific` member, because you already hold the number and a catch-all member would throw it away. `DescribeModeOfOperation` never returns null and keeps the number: `"Manufacturer-specific (-2)"`, `"Reserved (12)"`.

## What it does not do

It does not track the state machine across calls, does not know your drive's vendor objects, and does not decide what command to send next — the state a command produces depends on the state the drive was already in, which a single word cannot tell you. And it does no I/O: reading `0x6041` off a bus is the caller's job.

If that bus is EtherCAT over TwinCAT ADS, [`Dahlke.EtherCAT.Diagnostics`](https://www.nuget.org/packages/Dahlke.EtherCAT.Diagnostics) has `ReadCia402StatusAsync`, which does the CoE read and returns a `Cia402Status` decoded by this package. The dependency runs that way only — a decoder that needed a transport would be useless to everyone not using that transport.

## Links

- Source, issues and the other packages in this repository: <https://github.com/patdhlk/Dahlke.TwinCAT.Ads>
- Changelog: <https://github.com/patdhlk/Dahlke.TwinCAT.Ads/blob/main/CHANGELOG.md>

Apache-2.0.
