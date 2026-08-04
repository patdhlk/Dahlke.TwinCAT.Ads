using Dahlke.EtherCAT.Diagnostics;
using FluentAssertions;

namespace Dahlke.EtherCAT.Diagnostics.Tests;

public class BeckhoffDeviceDecoderTests
{
    // -- EK series (couplers) — family code 0x2C52 --

    [Fact]
    public void EK1100_coupler_decoded_correctly()
    {
        // Product code from real hardware: 0x044C2C52
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x044C2C52).Should().Be("EK1100");
    }

    [Fact]
    public void EK1110_extension_decoded_correctly()
    {
        // 1110 = 0x0456, family = 0x2C52
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x04562C52).Should().Be("EK1110");
    }

    [Fact]
    public void EK1122_junction_decoded_correctly()
    {
        // 1122 = 0x0462, family = 0x2C52
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x04622C52).Should().Be("EK1122");
    }

    // -- EL series (standard terminals) — family code 0x3052 --

    [Fact]
    public void EL1008_digital_input_decoded_correctly()
    {
        // Product code from real hardware: 0x03F03052
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x03F03052).Should().Be("EL1008");
    }

    [Fact]
    public void EL1809_digital_input_decoded_correctly()
    {
        // 1809 = 0x0711
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x07113052).Should().Be("EL1809");
    }

    [Fact]
    public void EL2808_digital_output_decoded_correctly()
    {
        // Product code from real hardware: 0x0AF83052
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x0AF83052).Should().Be("EL2808");
    }

    [Fact]
    public void EL2004_digital_output_decoded_correctly()
    {
        // 2004 = 0x07D4
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x07D43052).Should().Be("EL2004");
    }

    [Fact]
    public void EL3001_analog_input_decoded_correctly()
    {
        // 3001 = 0x0BB9
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x0BB93052).Should().Be("EL3001");
    }

    [Fact]
    public void EL3204_analog_input_decoded_correctly()
    {
        // 3204 = 0x0C84
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x0C843052).Should().Be("EL3204");
    }

    [Fact]
    public void EL4001_analog_output_decoded_correctly()
    {
        // 4001 = 0x0FA1
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x0FA13052).Should().Be("EL4001");
    }

    [Fact]
    public void EL5152_encoder_decoded_correctly()
    {
        // 5152 = 0x1420
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x14203052).Should().Be("EL5152");
    }

    [Fact]
    public void EL6021_serial_decoded_correctly()
    {
        // 6021 = 0x1785
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x17853052).Should().Be("EL6021");
    }

    [Fact]
    public void EL7031_stepper_decoded_correctly()
    {
        // 7031 = 0x1B77
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x1B773052).Should().Be("EL7031");
    }

    [Fact]
    public void EL9110_power_supply_decoded_correctly()
    {
        // 9110 = 0x2396
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x23963052).Should().Be("EL9110");
    }

    // -- EP series (IP67 box modules) — family code 0x4052 --

    [Fact]
    public void EP3174_ip67_analog_decoded_correctly()
    {
        // 3174 = 0x0C66
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x0C664052).Should().Be("EP3174");
    }

    // -- EJ series (plug-in modules) — family code 0x6052 --

    [Fact]
    public void EJ1008_plugin_decoded_correctly()
    {
        // 1008 = 0x03F0
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x03F06052).Should().Be("EJ1008");
    }

    // -- Non-Beckhoff devices --

    [Fact]
    public void NonBeckhoff_vendor_returns_vendor_description()
    {
        BeckhoffDeviceDecoder.DecodeDeviceType(0x00000089, 0x12345678)
            .Should().Be("Vendor(0x89)");
    }

    [Fact]
    public void Zero_vendor_returns_Unknown()
    {
        BeckhoffDeviceDecoder.DecodeDeviceType(0, 0x12345678).Should().Be("Unknown");
    }

    [Fact]
    public void Zero_product_code_returns_Unknown()
    {
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x00000000).Should().Be("Unknown");
    }

    [Fact]
    public void Zero_terminal_number_with_family_code_returns_Unknown()
    {
        // Terminal number = 0, family code = EL
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x00003052).Should().Be("Unknown");
    }

    // -- Unrecognized family code fallback --

    [Fact]
    public void Unknown_family_code_with_coupler_range_falls_back_to_EK()
    {
        // Terminal 1100 with unknown family code 0x9999
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x044C9999).Should().Be("EK1100");
    }

    [Fact]
    public void Unknown_family_code_with_io_range_falls_back_to_EL()
    {
        // Terminal 2004 with unknown family code 0xABCD
        BeckhoffDeviceDecoder.DecodeDeviceType(0x2, 0x07D4ABCD).Should().Be("EL2004");
    }
}
