using System.Globalization;

namespace Dahlke.EtherCAT.Cia402;

/// <summary>
/// CiA-402 (DS402) drive profile decoders: statusword, controlword and modes of operation.
///
/// <para>
/// <b>Pure functions over integers.</b> Nothing here does I/O, holds state, allocates a connection or
/// can fail: a word goes in and a decoded value comes out, deterministically. That is why the package
/// has no dependencies and no configuration — a caller reading <c>0x6041</c> over EtherCAT CoE, SoE,
/// CANopen, a serial gateway or yesterday's log file uses exactly the same code.
/// </para>
/// <para>
/// <b>What this does not do.</b> It does not know your drive's vendor objects, does not track the
/// state machine across calls, and does not decide what command to send next. The state a drive
/// reaches from a given command depends on the state it was in, which one word cannot tell you.
/// </para>
/// </summary>
public static class Cia402
{
    // Statusword bit positions (object 0x6041). The four state-machine bits 0-3 and bits 5-6 are
    // consumed by the mask table below rather than named here, because their MEANING is only the
    // combination, not the individual bit: bit 0 alone says nothing.
    private const ushort VoltageEnabledBit = 0x0010;      // bit 4
    private const ushort WarningBit = 0x0080;             // bit 7
    private const ushort RemoteBit = 0x0200;              // bit 9
    private const ushort TargetReachedBit = 0x0400;       // bit 10
    private const ushort InternalLimitBit = 0x0800;       // bit 11

    // Controlword bits (object 0x6040).
    private const ushort SwitchOnBit = 0x0001;            // bit 0
    private const ushort EnableVoltageBit = 0x0002;       // bit 1
    private const ushort QuickStopBit = 0x0004;           // bit 2
    private const ushort EnableOperationBit = 0x0008;     // bit 3
    private const ushort FaultResetBit = 0x0080;          // bit 7
    private const ushort HaltBit = 0x0100;                // bit 8

    /// <summary>
    /// Decodes a statusword (object <c>0x6041</c>) into the drive's state and flags.
    ///
    /// <para>
    /// A word matching no row of the CiA-402 state table decodes to
    /// <see cref="Cia402State.Unknown"/> rather than the nearest row — the table does not cover all
    /// 65536 words, and drives do produce words outside it. The flag bits are decoded either way.
    /// </para>
    /// </summary>
    /// <param name="statusword">The two bytes of <c>0x6041</c>, as a little-endian UINT16.</param>
    public static Cia402Status DecodeStatusword(ushort statusword)
    {
        return new Cia402Status(
            StateOf(statusword),
            (statusword & VoltageEnabledBit) != 0,
            (statusword & WarningBit) != 0,
            (statusword & RemoteBit) != 0,
            (statusword & TargetReachedBit) != 0,
            (statusword & InternalLimitBit) != 0);
    }

    /// <summary>
    /// Describes a statusword in one line: the state, then the flags that are set.
    ///
    /// <para>
    /// For an unnamed word the description carries the raw value —
    /// <c>"Unknown(0x1591): voltage enabled, warning, target reached"</c> — so it is never a dead end
    /// for whoever has to look the bits up.
    /// </para>
    /// </summary>
    /// <param name="statusword">The two bytes of <c>0x6041</c>, as a little-endian UINT16.</param>
    public static string DescribeStatusword(ushort statusword)
    {
        var status = DecodeStatusword(statusword);

        var name = status.State == Cia402State.Unknown
            ? string.Create(CultureInfo.InvariantCulture, $"Unknown(0x{statusword:X4})")
            : status.State.ToString();

        var flags = new List<string>(5);

        if (status.VoltageEnabled)
        {
            flags.Add("voltage enabled");
        }

        if (status.Warning)
        {
            flags.Add("warning");
        }

        if (status.Remote)
        {
            flags.Add("remote");
        }

        if (status.TargetReached)
        {
            flags.Add("target reached");
        }

        if (status.InternalLimitActive)
        {
            flags.Add("internal limit active");
        }

        return flags.Count == 0 ? name : $"{name}: {string.Join(", ", flags)}";
    }

    /// <summary>
    /// Decodes a controlword (object <c>0x6040</c>) into the command it carries and the halt bit.
    ///
    /// <para>
    /// Total over its input: every word names exactly one <see cref="Cia402Command"/>. Bit 7
    /// outranks everything else, and <c>0xxx0111</c> reports
    /// <see cref="Cia402Command.SwitchOn"/> — which is also the standard's Disable-operation
    /// command, the same word.
    /// </para>
    /// </summary>
    /// <param name="controlword">The two bytes of <c>0x6040</c>, as a little-endian UINT16.</param>
    public static Cia402Controlword DecodeControlword(ushort controlword)
    {
        return new Cia402Controlword(CommandOf(controlword), (controlword & HaltBit) != 0);
    }

    /// <summary>
    /// Describes a controlword in one line: the command, and <c>": halt"</c> when bit 8 is set.
    /// </summary>
    /// <param name="controlword">The two bytes of <c>0x6040</c>, as a little-endian UINT16.</param>
    public static string DescribeControlword(ushort controlword)
    {
        var decoded = DecodeControlword(controlword);

        return decoded.Halt ? $"{decoded.Command}: halt" : decoded.Command.ToString();
    }

    /// <summary>
    /// Builds the canonical controlword for a command — the inverse of
    /// <see cref="DecodeControlword"/>.
    ///
    /// <para>
    /// "Canonical" because most commands have don't-care bits and so several valid encodings; this
    /// returns the word the standard prints and a commissioning engineer types: <c>0x0006</c>,
    /// <c>0x0007</c>, <c>0x000F</c> for the usual enable sequence, and <c>0x0080</c> for a fault
    /// reset. Every don't-care bit is left clear, so <see cref="Cia402Controlword.Halt"/> is never
    /// set — a caller who wants a halt must set bit 8 themselves rather than have a command quietly
    /// stop the drive.
    /// </para>
    /// <para>
    /// The returned word is a LEVEL, and one command is not:
    /// <see cref="Cia402Command.FaultReset"/> acts on the rising edge of bit 7, so writing
    /// <c>0x0080</c> twice resets one fault, not two.
    /// </para>
    /// </summary>
    /// <param name="command">The command to encode.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="command"/> is not a member of <see cref="Cia402Command"/>. Every member has
    /// an encoding, so this only fires on a value cast in from outside the enum.
    /// </exception>
    public static ushort EncodeCommand(Cia402Command command)
    {
        return command switch
        {
            Cia402Command.DisableVoltage => 0x0000,
            Cia402Command.QuickStop => QuickStopEncoding,
            Cia402Command.Shutdown => ShutdownEncoding,
            Cia402Command.SwitchOn => SwitchOnEncoding,
            Cia402Command.EnableOperation => EnableOperationEncoding,
            Cia402Command.FaultReset => FaultResetBit,
            _ => throw new ArgumentOutOfRangeException(nameof(command), command,
                "Not a CiA-402 command."),
        };
    }

    // Quick stop is bit 1 set with bit 2 clear — "enable voltage, do not clear quick stop".
    private const ushort QuickStopEncoding = EnableVoltageBit;
    private const ushort ShutdownEncoding = EnableVoltageBit | QuickStopBit;
    private const ushort SwitchOnEncoding = SwitchOnBit | EnableVoltageBit | QuickStopBit;
    private const ushort EnableOperationEncoding = SwitchOnEncoding | EnableOperationBit;

    /// <summary>
    /// Decodes a modes-of-operation byte (<c>0x6060</c> written, <c>0x6061</c> read back).
    ///
    /// <para>
    /// Returns <see langword="null"/> — not a catch-all member — for a value CiA-402 does not define:
    /// the reserved <c>5</c>, anything from <c>12</c> up, and every negative value, which the standard
    /// hands to the manufacturer. A <c>ManufacturerSpecific</c> member would throw away the only part
    /// of a vendor mode that carries information, its number, which the caller already holds in
    /// <paramref name="mode"/>. Use <see cref="DescribeModeOfOperation"/> for a string that names the
    /// undefined cases and keeps the number.
    /// </para>
    /// </summary>
    /// <param name="mode">The signed byte read from or written to the object.</param>
    public static Cia402Mode? DecodeModeOfOperation(sbyte mode)
    {
        var candidate = (Cia402Mode)mode;

        return Enum.IsDefined(candidate) ? candidate : null;
    }

    /// <summary>
    /// Describes a modes-of-operation byte, with the abbreviation the standard and the drive manuals
    /// use: <c>"Cyclic Synchronous Position (csp)"</c>.
    ///
    /// <para>
    /// Never empty and never null. An undefined value is named for what it is and keeps its number —
    /// <c>"Reserved (12)"</c>, <c>"Manufacturer-specific (-2)"</c>.
    /// </para>
    /// </summary>
    /// <param name="mode">The signed byte read from or written to the object.</param>
    public static string DescribeModeOfOperation(sbyte mode)
    {
        return DecodeModeOfOperation(mode) switch
        {
            Cia402Mode.NoMode => "No mode",
            Cia402Mode.ProfilePosition => "Profile Position (pp)",
            Cia402Mode.Velocity => "Velocity (vl)",
            Cia402Mode.ProfileVelocity => "Profile Velocity (pv)",
            Cia402Mode.ProfileTorque => "Profile Torque (tq)",
            Cia402Mode.Homing => "Homing (hm)",
            Cia402Mode.InterpolatedPosition => "Interpolated Position (ip)",
            Cia402Mode.CyclicSyncPosition => "Cyclic Synchronous Position (csp)",
            Cia402Mode.CyclicSyncVelocity => "Cyclic Synchronous Velocity (csv)",
            Cia402Mode.CyclicSyncTorque => "Cyclic Synchronous Torque (cst)",
            Cia402Mode.CyclicSyncTorqueWithCommutationAngle =>
                "Cyclic Synchronous Torque with Commutation Angle (cstca)",

            // Negative is the manufacturer's range by definition; everything else undefined is
            // reserved by the standard, whether it is the lone 5 or anything from 12 up.
            _ => mode < 0
                ? string.Create(CultureInfo.InvariantCulture, $"Manufacturer-specific ({mode})")
                : string.Create(CultureInfo.InvariantCulture, $"Reserved ({mode})"),
        };
    }

    /// <summary>
    /// The CiA-402 state table, applied.
    ///
    /// <para>
    /// The standard prints eight rows of bit patterns with don't-cares (bit 15 leftmost):
    /// </para>
    /// <code>
    ///   Not ready to switch on   xxxx xxxx x0xx 0000     mask 0x004F  value 0x0000
    ///   Switch on disabled       xxxx xxxx x1xx 0000     mask 0x004F  value 0x0040
    ///   Ready to switch on       xxxx xxxx x01x 0001     mask 0x006F  value 0x0021
    ///   Switched on              xxxx xxxx x01x 0011     mask 0x006F  value 0x0023
    ///   Operation enabled        xxxx xxxx x01x 0111     mask 0x006F  value 0x0027
    ///   Quick stop active        xxxx xxxx x00x 0111     mask 0x006F  value 0x0007
    ///   Fault reaction active    xxxx xxxx x0xx 1111     mask 0x004F  value 0x000F
    ///   Fault                    xxxx xxxx x0xx 1000     mask 0x004F  value 0x0008
    /// </code>
    /// <para>
    /// Two masks, so two switches: the four rows that pin bit 5 (quick stop, which is ACTIVE LOW —
    /// set means "not quick-stopping") are tested against <c>0x006F</c>, and the four that leave it
    /// free against <c>0x004F</c>. The eight rows are mutually exclusive, so the nesting is for
    /// readability rather than precedence, and a test sweeps all 65536 words against the table as
    /// written above to keep that true.
    /// </para>
    /// </summary>
    private static Cia402State StateOf(ushort statusword)
    {
        return (statusword & 0x006F) switch
        {
            0x0021 => Cia402State.ReadyToSwitchOn,
            0x0023 => Cia402State.SwitchedOn,
            0x0027 => Cia402State.OperationEnabled,
            0x0007 => Cia402State.QuickStopActive,

            _ => (statusword & 0x004F) switch
            {
                0x0000 => Cia402State.NotReadyToSwitchOn,
                0x0040 => Cia402State.SwitchOnDisabled,
                0x000F => Cia402State.FaultReactionActive,
                0x0008 => Cia402State.Fault,

                _ => Cia402State.Unknown,
            },
        };
    }

    /// <summary>
    /// The CiA-402 command table, applied as a decision tree.
    ///
    /// <para>
    /// The standard's rows, with their don't-cares:
    /// </para>
    /// <code>
    ///   Fault reset          1xxx xxxx xxxx xxxx     bit 7 set
    ///   Disable voltage      0xxx xxxx xxxx xx0x     bit 1 clear
    ///   Quick stop           0xxx xxxx xxxx x01x     bit 1 set, bit 2 clear
    ///   Shutdown             0xxx xxxx xxxx x110     bits 1,2 set, bit 0 clear
    ///   Switch on            0xxx xxxx xxxx 0111     bits 0,1,2 set, bit 3 clear
    ///   Enable operation     0xxx xxxx xxxx 1111     bits 0,1,2,3 set
    /// </code>
    /// <para>
    /// Written as a tree rather than a mask table because the rows are a strict refinement of one
    /// another once bit 7 is out of the way, which makes the tree both shorter and obviously TOTAL:
    /// every word falls out of exactly one branch, so there is no unreachable case to name. A test
    /// checks it against the mask table above over the whole domain.
    /// </para>
    /// </summary>
    private static Cia402Command CommandOf(ushort controlword)
    {
        // Bit 7 first, and unconditionally: the standard gives fault reset every other bit as a
        // don't-care, so it wins over whatever pattern the low nibble spells.
        if ((controlword & FaultResetBit) != 0)
        {
            return Cia402Command.FaultReset;
        }

        if ((controlword & EnableVoltageBit) == 0)
        {
            return Cia402Command.DisableVoltage;
        }

        if ((controlword & QuickStopBit) == 0)
        {
            return Cia402Command.QuickStop;
        }

        if ((controlword & SwitchOnBit) == 0)
        {
            return Cia402Command.Shutdown;
        }

        // The last two rows differ only in bit 3, and the "switch on" half is also the standard's
        // Disable-operation command — see Cia402Command.SwitchOn.
        return (controlword & EnableOperationBit) == 0
            ? Cia402Command.SwitchOn
            : Cia402Command.EnableOperation;
    }
}
