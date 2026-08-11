namespace Dahlke.EtherCAT.Cia402.Tests;

/// <summary>
/// Modes of operation — object 0x6060 (the mode asked for) and 0x6061 (the mode the drive is
/// actually in). Both are a signed byte, and the sign is meaningful: the standard reserves every
/// negative value for the manufacturer.
/// </summary>
public class Cia402ModeTests
{
    [Theory]
    [InlineData(0, Cia402Mode.NoMode)]
    [InlineData(1, Cia402Mode.ProfilePosition)]
    [InlineData(2, Cia402Mode.Velocity)]
    [InlineData(3, Cia402Mode.ProfileVelocity)]
    [InlineData(4, Cia402Mode.ProfileTorque)]
    [InlineData(6, Cia402Mode.Homing)]
    [InlineData(7, Cia402Mode.InterpolatedPosition)]
    [InlineData(8, Cia402Mode.CyclicSyncPosition)]
    [InlineData(9, Cia402Mode.CyclicSyncVelocity)]
    [InlineData(10, Cia402Mode.CyclicSyncTorque)]
    [InlineData(11, Cia402Mode.CyclicSyncTorqueWithCommutationAngle)]
    public void Every_standard_mode_decodes_to_its_member(int mode, Cia402Mode expected)
    {
        Assert.Equal(expected, MotionCia402.DecodeModeOfOperation((sbyte)mode));
    }

    [Theory]
    [InlineData(5)]     // the one reserved value inside the standard range
    [InlineData(12)]
    [InlineData(127)]
    [InlineData(-1)]    // manufacturer-specific, and a real one: several drives use -1 or -2
    [InlineData(-2)]
    [InlineData(-128)]
    public void A_mode_the_standard_does_not_define_decodes_to_null(int mode)
    {
        // Null rather than a catch-all member, because the caller still holds the raw sbyte and a
        // member named ManufacturerSpecific would throw the NUMBER away — which is the only part of
        // a vendor mode that means anything.
        Assert.Null(MotionCia402.DecodeModeOfOperation((sbyte)mode));
    }

    [Fact]
    public void Decode_covers_the_whole_sbyte_range_without_throwing()
    {
        for (int mode = sbyte.MinValue; mode <= sbyte.MaxValue; mode++)
        {
            var decoded = MotionCia402.DecodeModeOfOperation((sbyte)mode);

            // Either it is a defined member, or it is null. Never an undefined enum value.
            if (decoded is not null)
            {
                Assert.True(Enum.IsDefined(decoded.Value), $"{mode} decoded to {(int)decoded.Value}");
                Assert.Equal(mode, (int)decoded.Value);
            }
        }
    }

    [Theory]
    [InlineData(0, "No mode")]
    [InlineData(1, "Profile Position (pp)")]
    [InlineData(2, "Velocity (vl)")]
    [InlineData(3, "Profile Velocity (pv)")]
    [InlineData(4, "Profile Torque (tq)")]
    [InlineData(6, "Homing (hm)")]
    [InlineData(7, "Interpolated Position (ip)")]
    [InlineData(8, "Cyclic Synchronous Position (csp)")]
    [InlineData(9, "Cyclic Synchronous Velocity (csv)")]
    [InlineData(10, "Cyclic Synchronous Torque (cst)")]
    [InlineData(11, "Cyclic Synchronous Torque with Commutation Angle (cstca)")]
    public void Describe_gives_the_name_and_the_abbreviation_the_standard_uses(int mode, string expected)
    {
        // The abbreviations are worth carrying: a drive's own manual and its ESI file label the
        // modes "csp" and "pv", not "CyclicSyncPosition".
        Assert.Equal(expected, MotionCia402.DescribeModeOfOperation((sbyte)mode));
    }

    [Theory]
    [InlineData(5, "Reserved (5)")]
    [InlineData(12, "Reserved (12)")]
    [InlineData(127, "Reserved (127)")]
    [InlineData(-1, "Manufacturer-specific (-1)")]
    [InlineData(-2, "Manufacturer-specific (-2)")]
    [InlineData(-128, "Manufacturer-specific (-128)")]
    public void Describe_names_an_undefined_mode_and_keeps_its_number(int mode, string expected)
    {
        Assert.Equal(expected, MotionCia402.DescribeModeOfOperation((sbyte)mode));
    }

    [Fact]
    public void Describe_never_returns_empty_for_any_mode()
    {
        for (int mode = sbyte.MinValue; mode <= sbyte.MaxValue; mode++)
        {
            Assert.False(string.IsNullOrWhiteSpace(MotionCia402.DescribeModeOfOperation((sbyte)mode)));
        }
    }

    [Fact]
    public void The_enum_is_backed_by_sbyte_so_it_can_hold_what_the_object_holds()
    {
        // 0x6060 and 0x6061 are SINT. An int-backed enum would silently admit values the object
        // cannot carry, so the width is part of the contract rather than an implementation detail.
        Assert.Equal(typeof(sbyte), Enum.GetUnderlyingType(typeof(Cia402Mode)));
    }
}
