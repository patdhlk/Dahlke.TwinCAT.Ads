namespace Dahlke.EtherCAT.Cia402.Tests;

/// <summary>
/// The CiA-402 statusword (object 0x6041) decode, row by row against the DS402 state table.
///
/// <para>
/// The table is reproduced here in the test rather than only in the implementation, on purpose. The
/// specification IS the oracle for this decode — there is no other way to check it — so the test's
/// job is to state the eight rows independently of the code that applies them, in the notation the
/// standard writes them in.
/// </para>
/// </summary>
public class Cia402StatuswordTests
{
    /// <summary>
    /// The eight rows of the DS402 state table, transcribed from the bit patterns the standard
    /// prints (bit 15 leftmost). <c>x</c> is a don't-care, and each row's mask is exactly the
    /// non-<c>x</c> positions.
    ///
    /// <code>
    ///   Not ready to switch on   xxxx xxxx x0xx 0000
    ///   Switch on disabled       xxxx xxxx x1xx 0000
    ///   Ready to switch on       xxxx xxxx x01x 0001
    ///   Switched on              xxxx xxxx x01x 0011
    ///   Operation enabled        xxxx xxxx x01x 0111
    ///   Quick stop active        xxxx xxxx x00x 0111
    ///   Fault reaction active    xxxx xxxx x0xx 1111
    ///   Fault                    xxxx xxxx x0xx 1000
    /// </code>
    ///
    /// Note that bit 5 (quick stop) is ACTIVE LOW: the three operational rows require it set, and
    /// the row where it is clear with bits 0-2 set is the quick stop itself.
    /// </summary>
    public static readonly (ushort Mask, ushort Value, Cia402State State)[] StateTable =
    [
        (0x004F, 0x0000, Cia402State.NotReadyToSwitchOn),
        (0x004F, 0x0040, Cia402State.SwitchOnDisabled),
        (0x006F, 0x0021, Cia402State.ReadyToSwitchOn),
        (0x006F, 0x0023, Cia402State.SwitchedOn),
        (0x006F, 0x0027, Cia402State.OperationEnabled),
        (0x006F, 0x0007, Cia402State.QuickStopActive),
        (0x004F, 0x000F, Cia402State.FaultReactionActive),
        (0x004F, 0x0008, Cia402State.Fault),
    ];

    [Theory]
    [InlineData(0x0000, Cia402State.NotReadyToSwitchOn)]
    [InlineData(0x0040, Cia402State.SwitchOnDisabled)]
    [InlineData(0x0021, Cia402State.ReadyToSwitchOn)]
    [InlineData(0x0023, Cia402State.SwitchedOn)]
    [InlineData(0x0027, Cia402State.OperationEnabled)]
    [InlineData(0x0007, Cia402State.QuickStopActive)]
    [InlineData(0x000F, Cia402State.FaultReactionActive)]
    [InlineData(0x0008, Cia402State.Fault)]
    public void Every_row_of_the_state_table_decodes_to_its_state(int statusword, Cia402State expected)
    {
        Assert.Equal(expected, Cia402.DecodeStatusword((ushort)statusword).State);
    }

    [Theory]
    // Bit 5 is a don't-care on all four of the masked-0x004F rows, so setting it must not move them.
    [InlineData(0x0020, Cia402State.NotReadyToSwitchOn)]
    [InlineData(0x0060, Cia402State.SwitchOnDisabled)]
    [InlineData(0x002F, Cia402State.FaultReactionActive)]
    [InlineData(0x0028, Cia402State.Fault)]
    // Bit 4 (voltage enabled) is a don't-care on every row.
    [InlineData(0x0031, Cia402State.ReadyToSwitchOn)]
    [InlineData(0x0037, Cia402State.OperationEnabled)]
    [InlineData(0x0017, Cia402State.QuickStopActive)]
    // Bit 7 (warning) likewise.
    [InlineData(0x00A1, Cia402State.ReadyToSwitchOn)]
    [InlineData(0x0088, Cia402State.Fault)]
    public void Dont_care_bits_do_not_change_the_state(int statusword, Cia402State expected)
    {
        Assert.Equal(expected, Cia402.DecodeStatusword((ushort)statusword).State);
    }

    /// <summary>
    /// The words hand-decoded during the Bonfiglioli ACU commissioning that prompted issue #74,
    /// pinned as literals. These are the whole reason this package exists, so they are the one set
    /// of inputs that is not synthesised from the table above.
    /// </summary>
    [Theory]
    [InlineData(0x0231, Cia402State.ReadyToSwitchOn)]
    [InlineData(0x0237, Cia402State.OperationEnabled)]
    [InlineData(0x0637, Cia402State.OperationEnabled)]
    [InlineData(0x0250, Cia402State.SwitchOnDisabled)]
    [InlineData(0x0218, Cia402State.Fault)]
    // 0x1591 matches NO row: bit 0 is set while bit 5 (quick stop) is clear, and the only row with
    // quick stop clear requires bits 0, 1 AND 2. A real drive produced it, and the honest answer is
    // that the standard does not name this combination — see Unknown's own documentation.
    [InlineData(0x1591, Cia402State.Unknown)]
    public void Words_read_off_the_commissioned_drive_decode_as_they_were_read_by_hand(
        int statusword, Cia402State expected)
    {
        Assert.Equal(expected, Cia402.DecodeStatusword((ushort)statusword).State);
    }

    [Fact]
    public void The_five_flag_bits_are_decoded_independently_of_the_state()
    {
        // Each flag is set on its own, so a mask error cannot hide behind another flag being set.
        Assert.True(Cia402.DecodeStatusword(0x0010).VoltageEnabled);
        Assert.True(Cia402.DecodeStatusword(0x0080).Warning);
        Assert.True(Cia402.DecodeStatusword(0x0200).Remote);
        Assert.True(Cia402.DecodeStatusword(0x0400).TargetReached);
        Assert.True(Cia402.DecodeStatusword(0x0800).InternalLimitActive);

        var none = Cia402.DecodeStatusword(0x0000);
        Assert.False(none.VoltageEnabled);
        Assert.False(none.Warning);
        Assert.False(none.Remote);
        Assert.False(none.TargetReached);
        Assert.False(none.InternalLimitActive);
    }

    [Fact]
    public void A_drive_reporting_every_flag_at_once_reports_every_flag()
    {
        // Operation enabled (0x0027) with the voltage bit and bits 7, 9, 10 and 11 all set.
        var status = Cia402.DecodeStatusword(0x0EB7);

        Assert.Equal(Cia402State.OperationEnabled, status.State);
        Assert.True(status.VoltageEnabled);
        Assert.True(status.Warning);
        Assert.True(status.Remote);
        Assert.True(status.TargetReached);
        Assert.True(status.InternalLimitActive);
    }

    /// <summary>
    /// Exhaustive over the whole input domain: 65536 words is small enough to simply try them all,
    /// which removes any argument about whether the chosen examples were representative.
    /// </summary>
    [Fact]
    public void Across_all_65536_words_the_flags_are_exactly_their_bits()
    {
        for (int word = 0; word <= ushort.MaxValue; word++)
        {
            var status = Cia402.DecodeStatusword((ushort)word);

            Assert.Equal((word & 0x0010) != 0, status.VoltageEnabled);
            Assert.Equal((word & 0x0080) != 0, status.Warning);
            Assert.Equal((word & 0x0200) != 0, status.Remote);
            Assert.Equal((word & 0x0400) != 0, status.TargetReached);
            Assert.Equal((word & 0x0800) != 0, status.InternalLimitActive);
        }
    }

    [Fact]
    public void Across_all_65536_words_Unknown_is_returned_exactly_when_no_row_matches()
    {
        for (int word = 0; word <= ushort.MaxValue; word++)
        {
            var matches = StateTable
                .Where(row => (word & row.Mask) == row.Value)
                .Select(row => row.State)
                .ToArray();

            // The eight rows are mutually exclusive, which is itself worth asserting: if a future
            // edit widened a mask, two rows could claim one word and the decode would depend on
            // the order they are tested in.
            Assert.True(matches.Length <= 1, $"0x{word:X4} matched {matches.Length} rows");

            var state = Cia402.DecodeStatusword((ushort)word).State;

            Assert.Equal(matches.Length == 1 ? matches[0] : Cia402State.Unknown, state);
        }
    }

    [Fact]
    public void Manufacturer_specific_and_operation_mode_specific_bits_do_not_affect_the_decode()
    {
        // Bits 8 and 12-15 are manufacturer or mode specific and carry nothing this decode reads,
        // so setting any of them must leave the result identical.
        const ushort Ignored = 0xF100;

        for (int word = 0; word <= ushort.MaxValue; word++)
        {
            Assert.Equal(
                Cia402.DecodeStatusword((ushort)(word & ~Ignored)),
                Cia402.DecodeStatusword((ushort)(word | Ignored)));
        }
    }

    [Theory]
    [InlineData(0x0027, "OperationEnabled")]
    [InlineData(0x0237, "OperationEnabled: voltage enabled, remote")]
    [InlineData(0x0637, "OperationEnabled: voltage enabled, remote, target reached")]
    [InlineData(0x0250, "SwitchOnDisabled: voltage enabled, remote")]
    [InlineData(0x0218, "Fault: voltage enabled, remote")]
    [InlineData(0x0008, "Fault")]
    [InlineData(0x0000, "NotReadyToSwitchOn")]
    // The unnamed combination carries its raw word, so a description is never a dead end: whoever
    // reads it can still look the bits up.
    [InlineData(0x1591, "Unknown(0x1591): voltage enabled, warning, target reached")]
    public void Describe_names_the_state_and_lists_the_flags_that_are_set(int statusword, string expected)
    {
        Assert.Equal(expected, Cia402.DescribeStatusword((ushort)statusword));
    }

    [Fact]
    public void Describe_never_returns_empty_for_any_word()
    {
        for (int word = 0; word <= ushort.MaxValue; word++)
        {
            Assert.False(string.IsNullOrWhiteSpace(Cia402.DescribeStatusword((ushort)word)));
        }
    }

    [Fact]
    public void Two_equal_words_decode_to_equal_records()
    {
        // Cia402Status is a record struct precisely so a caller can compare two polls for change
        // without writing a comparison. Worth pinning: it is the property a polling loop rests on.
        Assert.Equal(Cia402.DecodeStatusword(0x0637), Cia402.DecodeStatusword(0x0637));
        Assert.NotEqual(Cia402.DecodeStatusword(0x0637), Cia402.DecodeStatusword(0x0237));
    }
}
