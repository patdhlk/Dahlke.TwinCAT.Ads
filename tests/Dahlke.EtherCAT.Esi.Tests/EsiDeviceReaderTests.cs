using Dahlke.EtherCAT.Esi;
using FluentAssertions;

namespace Dahlke.EtherCAT.Esi.Tests;

public class EsiDeviceReaderTests
{
    private const uint Beckhoff = 2;
    private const uint El3204 = 0x0C843052;
    private const uint Ek1100 = 0x044C2C52;
    private const uint Rev1 = 0x00100000;

    private const uint El5001 = 0x13893052;

    private static string Fixture(string directory, string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", directory, name);

    private static string Fixture(string name) => Fixture("Esi", name);

    private static string El32xx => Fixture("Beckhoff EL32xx.xml");
    private static string Ek11xx => Fixture("Beckhoff EK11xx.xml");
    private static string El5xxxNoRevision => Fixture("EsiNoRevision", "Beckhoff EL5xxx.xml");

    // The reference implementation takes the HIGHEST revision unconditionally and would return
    // rev17 here. #60 requires the exact match to win, so this is the test that pins the
    // behavioural difference.
    [Fact]
    public async Task TryReadAsync_prefers_an_exact_revision_over_a_higher_one()
    {
        var device = await EsiDeviceReader.TryReadAsync(El32xx, new EsiKey(Beckhoff, El3204, Rev1));

        device.Should().NotBeNull();
        device!.Url.Should().Be("http://www.beckhoff.de/EL3204-rev1");
    }

    [Fact]
    public async Task TryReadAsync_falls_back_to_the_highest_revision_when_none_match_exactly()
    {
        var device = await EsiDeviceReader.TryReadAsync(
            El32xx, new EsiKey(Beckhoff, El3204, 0x00990000));

        device.Should().NotBeNull();
        device!.Url.Should().Be("http://www.beckhoff.de/EL3204-rev17");
    }

    // #60's fallback-to-highest-revision path is initialised with bestRevision = -1, and
    // ParseHex also returns -1 for a RevisionNo that is absent — which it legitimately can be,
    // since RevisionNo is optional in the ESI schema. A device whose only matching entry omits
    // RevisionNo must still resolve via the fallback, not be silently indistinguishable from a
    // device that plain isn't in the file.
    [Fact]
    public async Task TryReadAsync_resolves_a_device_whose_type_omits_revision_no()
    {
        var device = await EsiDeviceReader.TryReadAsync(
            El5xxxNoRevision, new EsiKey(Beckhoff, El5001, Rev1));

        device.Should().NotBeNull();
        device!.NameEn.Should().Be("EL5001 SSI Encoder Interface");
    }

    [Fact]
    public async Task TryReadAsync_maps_every_identity_field()
    {
        var device = await EsiDeviceReader.TryReadAsync(El32xx, new EsiKey(Beckhoff, El3204, Rev1));

        device.Should().NotBeNull();
        device!.VendorName.Should().Be("Beckhoff Automation GmbH & Co. KG");
        device.NameEn.Should().Be("EL3204 4Ch. Ana. Input PT100 (RTD)");
        device.Group.Should().Be("Analog Input");
    }

    // Proves the file's declared ISO-8859-1 encoding is honoured rather than the bytes being read
    // as UTF-8, which would yield mojibake here.
    [Fact]
    public async Task TryReadAsync_honours_the_declared_iso_8859_1_encoding()
    {
        var device = await EsiDeviceReader.TryReadAsync(El32xx, new EsiKey(Beckhoff, El3204, Rev1));

        device.Should().NotBeNull();
        device!.NameDe.Should().Be("EL3204 4K. Ana. Eingang PT100 (RTD) Meßbereich");
    }

    [Fact]
    public async Task TryReadAsync_returns_null_for_a_product_code_the_file_does_not_describe()
    {
        var device = await EsiDeviceReader.TryReadAsync(
            El32xx, new EsiKey(Beckhoff, 0x99993052, Rev1));

        device.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_returns_null_when_the_vendor_id_does_not_match()
    {
        var device = await EsiDeviceReader.TryReadAsync(El32xx, new EsiKey(999, El3204, Rev1));

        device.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_reports_an_absent_url_as_null_not_empty()
    {
        var device = await EsiDeviceReader.TryReadAsync(Ek11xx, new EsiKey(Beckhoff, Ek1100, Rev1));

        device.Should().NotBeNull();
        device!.Url.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_reports_an_absent_german_name_as_null()
    {
        var device = await EsiDeviceReader.TryReadAsync(Ek11xx, new EsiKey(Beckhoff, Ek1100, Rev1));

        device.Should().NotBeNull();
        device!.NameDe.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_reports_an_unresolvable_group_type_as_null()
    {
        var device = await EsiDeviceReader.TryReadAsync(Ek11xx, new EsiKey(Beckhoff, Ek1100, Rev1));

        device.Should().NotBeNull();
        device!.Group.Should().BeNull();
    }

    // An EtherCATInfoList-rooted file holds several <EtherCATInfo>/<Vendor> sections. The first
    // section here matches the key's vendor and holds a fallback (non-exact-revision) match; the
    // second belongs to a different vendor entirely. #61's fix must not let that second section's
    // mismatch discard the match already found in the first — and must attribute the match to
    // the FIRST section's vendor, not the second's, which is why the two sections deliberately
    // carry distinct vendor names below.
    [Fact]
    public async Task TryReadAsync_keeps_a_match_from_an_earlier_section_despite_a_later_vendor_mismatch()
    {
        string path = Path.Combine(Path.GetTempPath(), $"esi-multi-vendor-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(
            path,
            """
            <?xml version="1.0" encoding="ISO-8859-1"?>
            <EtherCATInfoList>
              <EtherCATInfo>
                <Vendor>
                  <Id>#x00000002</Id>
                  <Name LcId="1033">Beckhoff Automation GmbH &amp; Co. KG</Name>
                </Vendor>
                <Descriptions>
                  <Groups>
                    <Group>
                      <Type>AnaIn</Type>
                      <Name LcId="1033">Analog Input</Name>
                    </Group>
                  </Groups>
                  <Devices>
                    <Device>
                      <Type ProductCode="#x0c843052" RevisionNo="#x00050000">EL3204</Type>
                      <Name LcId="1033">EL3204 4Ch. Ana. Input PT100 (RTD)</Name>
                      <GroupType>AnaIn</GroupType>
                      <URL>http://www.beckhoff.de/EL3204</URL>
                    </Device>
                  </Devices>
                </Descriptions>
              </EtherCATInfo>
              <EtherCATInfo>
                <Vendor>
                  <Id>#x00000099</Id>
                  <Name LcId="1033">Some Other Vendor</Name>
                </Vendor>
                <Descriptions>
                  <Devices>
                    <Device>
                      <Type ProductCode="#x00010001">OtherDevice</Type>
                      <Name LcId="1033">Other Device</Name>
                    </Device>
                  </Devices>
                </Descriptions>
              </EtherCATInfo>
            </EtherCATInfoList>
            """);

        try
        {
            // Rev1 (0x00100000) does not exactly match section 1's device (0x00050000), so the
            // reader must fall through to the second section's Vendor mismatch before returning —
            // exercising the exact branch #61 fixed, rather than returning early via an exact hit.
            var device = await EsiDeviceReader.TryReadAsync(path, new EsiKey(Beckhoff, El3204, Rev1));

            device.Should().NotBeNull();
            device!.NameEn.Should().Be("EL3204 4Ch. Ana. Input PT100 (RTD)");
            device.VendorName.Should().Be("Beckhoff Automation GmbH & Co. KG");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TryReadAsync_throws_for_malformed_xml()
    {
        string path = Path.Combine(Path.GetTempPath(), $"esi-malformed-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(path, "<EtherCATInfo><Vendor><Id>#x00000002</Id>");

        try
        {
            Func<Task> read = () => EsiDeviceReader.TryReadAsync(path, new EsiKey(Beckhoff, El3204, Rev1));

            await read.Should().ThrowAsync<System.Xml.XmlException>();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
