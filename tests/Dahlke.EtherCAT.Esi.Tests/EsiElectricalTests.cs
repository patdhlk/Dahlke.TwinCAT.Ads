using FluentAssertions;

namespace Dahlke.EtherCAT.Esi.Tests;

public class EsiElectricalTests
{
    private const uint Beckhoff = 2;
    private const uint El3204 = 0x0C843052;
    private const uint El3200 = 0x0C803052;
    private const uint El3205 = 0x0C853052;
    private const uint Ek1100 = 0x044C2C52;
    private const uint Rev1 = 0x00100000;

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi", name);

    private static string El32xx => Fixture("Beckhoff EL32xx.xml");
    private static string Ek11xx => Fixture("Beckhoff EK11xx.xml");

    [Fact]
    public async Task TryReadAsync_reports_the_declared_ebus_current_in_ma()
    {
        var device = await EsiDeviceReader.TryReadAsync(El32xx, new EsiKey(Beckhoff, El3204, Rev1));

        device.Should().NotBeNull();
        device!.EBusCurrentMa.Should().Be(190);
    }

    // A coupler SUPPLIES the E-bus, which ESI states as a negative draw. EK1100's own name reads
    // "0.5A E-Bus" and its ESI declares -500, so the sign is the vendor's convention, not one
    // invented here.
    [Fact]
    public async Task TryReadAsync_reports_a_supply_device_as_a_negative_draw()
    {
        var device = await EsiDeviceReader.TryReadAsync(Ek11xx, new EsiKey(Beckhoff, Ek1100, Rev1));

        device.Should().NotBeNull();
        device!.EBusCurrentMa.Should().Be(-500);
    }

    // The whole point of #65. A consumer summing draws across an E-bus segment cannot tell an
    // unknown contributor from one that genuinely draws nothing if absence reports as 0 — the
    // same defect class as #61.
    [Fact]
    public async Task TryReadAsync_reports_an_absent_ebus_current_as_null_not_zero()
    {
        var device = await EsiDeviceReader.TryReadAsync(El32xx, new EsiKey(Beckhoff, El3205, Rev1));

        device.Should().NotBeNull();
        device!.EBusCurrentMa.Should().BeNull();
    }

    // The other half of that distinction: a DECLARED zero must survive as zero.
    [Fact]
    public async Task TryReadAsync_reports_a_declared_zero_ebus_current_as_zero()
    {
        var device = await EsiDeviceReader.TryReadAsync(El32xx, new EsiKey(Beckhoff, El3200, Rev1));

        device.Should().NotBeNull();
        device!.EBusCurrentMa.Should().Be(0);
    }
}
