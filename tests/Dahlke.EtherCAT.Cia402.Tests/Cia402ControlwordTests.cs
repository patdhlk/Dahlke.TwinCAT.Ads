namespace Dahlke.EtherCAT.Cia402.Tests;

/// <summary>
/// The CiA-402 controlword (object 0x6040) decode and its inverse.
///
/// <para>
/// Unlike the statusword, this decode is TOTAL: every one of the 65536 words names exactly one
/// command, which is why <see cref="Cia402Command"/> has no <c>Unknown</c> member to test for. The
/// sweep at the bottom is what holds that claim up.
/// </para>
/// </summary>
public class Cia402ControlwordTests
{
    /// <summary>
    /// The command table as the standard prints it, bit 15 leftmost:
    ///
    /// <code>
    ///   Shutdown             0xxx xxxx x xxx x110
    ///   Switch on            0xxx xxxx x xxx 0111
    ///   Disable voltage      0xxx xxxx x xxx xx0x
    ///   Quick stop           0xxx xxxx x xxx x01x
    ///   Disable operation    0xxx xxxx x xxx 0111
    ///   Enable operation     0xxx xxxx x xxx 1111
    ///   Fault reset          1xxx xxxx x xxx xxxx
    /// </code>
    ///
    /// Seven rows, six commands: "Switch on" and "Disable operation" are the SAME bit pattern
    /// (transitions 3 and 5), told apart only by the state the drive is in — which a decoder of one
    /// word cannot know. See <see cref="Cia402Command.SwitchOn"/>.
    /// </summary>
    public static readonly (ushort Mask, ushort Value, Cia402Command Command)[] CommandTable =
    [
        (0x0080, 0x0080, Cia402Command.FaultReset),
        (0x0082, 0x0000, Cia402Command.DisableVoltage),
        (0x0086, 0x0002, Cia402Command.QuickStop),
        (0x0087, 0x0006, Cia402Command.Shutdown),
        (0x008F, 0x0007, Cia402Command.SwitchOn),
        (0x008F, 0x000F, Cia402Command.EnableOperation),
    ];

    [Theory]
    [InlineData(0x0000, Cia402Command.DisableVoltage)]
    [InlineData(0x0002, Cia402Command.QuickStop)]
    [InlineData(0x0006, Cia402Command.Shutdown)]
    [InlineData(0x0007, Cia402Command.SwitchOn)]
    [InlineData(0x000F, Cia402Command.EnableOperation)]
    [InlineData(0x0080, Cia402Command.FaultReset)]
    public void The_canonical_word_for_each_command_decodes_to_it(int controlword, Cia402Command expected)
    {
        Assert.Equal(expected, MotionCia402.DecodeControlword((ushort)controlword).Command);
    }

    [Theory]
    // Bit 3 is a don't-care for Shutdown and Quick stop, bit 0 for Quick stop and Disable voltage.
    [InlineData(0x000E, Cia402Command.Shutdown)]
    [InlineData(0x0003, Cia402Command.QuickStop)]
    [InlineData(0x000A, Cia402Command.QuickStop)]
    [InlineData(0x0001, Cia402Command.DisableVoltage)]
    [InlineData(0x0004, Cia402Command.DisableVoltage)]
    [InlineData(0x000D, Cia402Command.DisableVoltage)]
    public void Dont_care_bits_do_not_change_the_command(int controlword, Cia402Command expected)
    {
        Assert.Equal(expected, MotionCia402.DecodeControlword((ushort)controlword).Command);
    }

    [Fact]
    public void Bit_7_is_a_fault_reset_whatever_else_is_set()
    {
        // The standard makes fault reset the rising edge of bit 7 with every other bit a don't-care,
        // so it outranks the pattern the low bits would otherwise spell. Checked over the whole
        // domain because it is the one row that overlaps all the others.
        for (int word = 0; word <= ushort.MaxValue; word++)
        {
            if ((word & 0x0080) == 0)
            {
                continue;
            }

            Assert.Equal(Cia402Command.FaultReset, MotionCia402.DecodeControlword((ushort)word).Command);
        }
    }

    [Fact]
    public void Halt_is_bit_8_and_nothing_else()
    {
        for (int word = 0; word <= ushort.MaxValue; word++)
        {
            Assert.Equal((word & 0x0100) != 0, MotionCia402.DecodeControlword((ushort)word).Halt);
        }
    }

    [Fact]
    public void Every_word_names_a_command_and_the_table_agrees_which()
    {
        for (int word = 0; word <= ushort.MaxValue; word++)
        {
            var command = MotionCia402.DecodeControlword((ushort)word).Command;

            // Total: there is no word this decode cannot name, so no member outside the enum can
            // ever come back.
            Assert.True(Enum.IsDefined(typeof(Cia402Command), command), $"0x{word:X4} decoded to {(int)command}");

            // The table's rows overlap by design (fault reset covers half the domain, and disable
            // voltage's mask is narrower than shutdown's), so the FIRST matching row is the answer —
            // the order in CommandTable is the standard's own precedence.
            var expected = CommandTable.First(row => (word & row.Mask) == row.Value).Command;

            Assert.Equal(expected, command);
        }
    }

    [Theory]
    [InlineData(Cia402Command.DisableVoltage, 0x0000)]
    [InlineData(Cia402Command.QuickStop, 0x0002)]
    [InlineData(Cia402Command.Shutdown, 0x0006)]
    [InlineData(Cia402Command.SwitchOn, 0x0007)]
    [InlineData(Cia402Command.EnableOperation, 0x000F)]
    [InlineData(Cia402Command.FaultReset, 0x0080)]
    public void Encode_produces_the_word_a_drive_expects(Cia402Command command, int expected)
    {
        Assert.Equal((ushort)expected, MotionCia402.EncodeCommand(command));
    }

    [Fact]
    public void Encode_and_decode_round_trip_for_every_command()
    {
        // The property that keeps the two tables honest: they are inverses, so an edit to one that
        // is not mirrored in the other fails here rather than in front of a drive.
        foreach (var command in Enum.GetValues(typeof(Cia402Command)).Cast<Cia402Command>())
        {
            Assert.Equal(command, MotionCia402.DecodeControlword(MotionCia402.EncodeCommand(command)).Command);
        }
    }

    [Fact]
    public void Encode_rejects_a_value_that_is_not_a_command()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MotionCia402.EncodeCommand((Cia402Command)99));
    }

    [Fact]
    public void Encoding_never_sets_halt()
    {
        // Halt is orthogonal to the command, and a caller that wants it must say so by setting bit 8
        // themselves. Encoding it silently would stop a drive that was asked to run.
        foreach (var command in Enum.GetValues(typeof(Cia402Command)).Cast<Cia402Command>())
        {
            Assert.False(MotionCia402.DecodeControlword(MotionCia402.EncodeCommand(command)).Halt);
        }
    }

    [Fact]
    public void The_three_word_enable_sequence_encodes_to_the_words_typed_during_commissioning()
    {
        // 6, 7, 15 — the sequence written into 0x6040 by hand to bring the ACU drive up. This test
        // is the whole point of EncodeCommand existing.
        Assert.Equal(0x0006, MotionCia402.EncodeCommand(Cia402Command.Shutdown));
        Assert.Equal(0x0007, MotionCia402.EncodeCommand(Cia402Command.SwitchOn));
        Assert.Equal(0x000F, MotionCia402.EncodeCommand(Cia402Command.EnableOperation));
    }

    [Theory]
    [InlineData(0x000F, "EnableOperation")]
    [InlineData(0x010F, "EnableOperation: halt")]
    [InlineData(0x0006, "Shutdown")]
    [InlineData(0x0106, "Shutdown: halt")]
    [InlineData(0x0080, "FaultReset")]
    [InlineData(0x0000, "DisableVoltage")]
    public void Describe_names_the_command_and_says_when_halt_is_set(int controlword, string expected)
    {
        Assert.Equal(expected, MotionCia402.DescribeControlword((ushort)controlword));
    }

    [Fact]
    public void Describe_never_returns_empty_for_any_word()
    {
        for (int word = 0; word <= ushort.MaxValue; word++)
        {
            Assert.False(string.IsNullOrWhiteSpace(MotionCia402.DescribeControlword((ushort)word)));
        }
    }
}
