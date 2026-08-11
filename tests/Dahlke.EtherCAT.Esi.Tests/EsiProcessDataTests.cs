using FluentAssertions;

namespace Dahlke.EtherCAT.Esi.Tests;

public class EsiProcessDataTests
{
    private const uint Beckhoff = 2;
    private const uint El3204 = 0x0C843052;
    private const uint El6001 = 0x17CD3052;
    private const uint El6002 = 0x17CE3052;
    private const uint Rev1 = 0x00100000;

    private static string El32xx =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi", "Beckhoff EL32xx.xml");

    private static string El6xxx =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "EsiDetail", "Beckhoff EL6xxx.xml");

    private static async Task<EsiProcessData> El3204ProcessDataAsync()
    {
        var device = await EsiDeviceReader.TryReadAsync(El32xx, new EsiKey(Beckhoff, El3204, Rev1));

        device.Should().NotBeNull();
        device!.ProcessData.Should().NotBeNull();

        return device.ProcessData!;
    }

    // #67: direction comes from the element name and is modelled explicitly, not left for a
    // consumer to infer from a string.
    [Fact]
    public async Task Parse_groups_pdos_by_direction_taken_from_the_element_name()
    {
        var processData = await El3204ProcessDataAsync();

        processData.Pdos.Should().HaveCount(2);
        processData.Pdos.Single(p => p.Direction == EsiPdoDirection.Transmit).Index.Should().Be(0x1A00);
        processData.Pdos.Single(p => p.Direction == EsiPdoDirection.Receive).Index.Should().Be(0x1600);
    }

    // The fixture deliberately lists <RxPdo> BEFORE <TxPdo> — a naive "collect all TxPdo, then
    // all RxPdo" implementation would still pass every other test in this file (they all match by
    // direction, not position), so this is the one test that pins the parser to the file's own
    // document order rather than to element name.
    [Fact]
    public async Task Parse_reports_pdos_in_the_files_own_document_order()
    {
        var processData = await El3204ProcessDataAsync();

        processData.Pdos[0].Direction.Should().Be(EsiPdoDirection.Receive);
        processData.Pdos[1].Direction.Should().Be(EsiPdoDirection.Transmit);
    }

    [Fact]
    public async Task Parse_reports_every_entry_field()
    {
        var processData = await El3204ProcessDataAsync();

        EsiPdo tx = processData.Pdos.Single(p => p.Direction == EsiPdoDirection.Transmit);
        tx.Name.Should().Be("RTD Inputs");
        tx.Fixed.Should().BeTrue();
        tx.Mandatory.Should().BeTrue();

        EsiPdoEntry value = tx.Entries[2];
        value.Index.Should().Be(0x6000);
        value.SubIndex.Should().Be(17);
        value.BitLength.Should().Be(16);
        value.Name.Should().Be("Value");
        value.DataType.Should().Be("INT");
    }

    // A padding entry is <Index>#x0</Index> with a bit length and nothing else. Dropping it would
    // silently corrupt any consumer computing bit offsets down the PDO, so it is kept with its
    // three absent fields reported as null.
    [Fact]
    public async Task Parse_keeps_padding_entries_rather_than_dropping_them()
    {
        var processData = await El3204ProcessDataAsync();

        EsiPdo tx = processData.Pdos.Single(p => p.Direction == EsiPdoDirection.Transmit);
        tx.Entries.Should().HaveCount(3);

        EsiPdoEntry padding = tx.Entries[1];
        padding.Index.Should().Be(0);
        padding.BitLength.Should().Be(7);
        padding.SubIndex.Should().BeNull();
        padding.Name.Should().BeNull();
        padding.DataType.Should().BeNull();
    }

    // The fixture's TxPdo carries a fourth <Entry> with an <Index> but no <BitLen> — malformed,
    // per EsiPdoEntry's own doc comment. It must be dropped while its three well-formed siblings
    // (Underrange, the padding entry, and Value) still come through; one bad entry must not cost
    // a caller the rest of the PDO.
    [Fact]
    public async Task Parse_drops_an_entry_with_no_bit_length_but_keeps_its_siblings()
    {
        var processData = await El3204ProcessDataAsync();

        EsiPdo tx = processData.Pdos.Single(p => p.Direction == EsiPdoDirection.Transmit);

        tx.Entries.Should().HaveCount(3);
        tx.Entries.Should().NotContain(e => e.Name == "Malformed entry with no BitLen");
        tx.Entries.Select(e => e.Name).Should().Equal(new[] { "Underrange", null, "Value" });
    }

    [Fact]
    public async Task Parse_reports_the_sync_manager_a_pdo_is_assigned_to()
    {
        var processData = await El3204ProcessDataAsync();

        processData.Pdos.Single(p => p.Direction == EsiPdoDirection.Transmit)
            .SyncManager.Should().Be(3);
    }

    // 37,541 of 58,128 PDOs in Beckhoff's published set carry no Sm attribute, so this is the
    // majority case, not an edge case.
    [Fact]
    public async Task Parse_reports_a_pdo_with_no_sm_attribute_as_unassigned()
    {
        var processData = await El3204ProcessDataAsync();

        processData.Pdos.Single(p => p.Direction == EsiPdoDirection.Receive)
            .SyncManager.Should().BeNull();
    }

    // The fixture's RxPdo carries Fixed="1" but no Mandatory attribute at all, so the nullable
    // must come back null rather than defaulting to false.
    [Fact]
    public async Task Parse_reports_no_mandatory_attribute_as_null()
    {
        var processData = await El3204ProcessDataAsync();

        EsiPdo rx = processData.Pdos.Single(p => p.Direction == EsiPdoDirection.Receive);
        rx.Fixed.Should().BeTrue();
        rx.Mandatory.Should().BeNull();
    }

    // The Sm number is the ORDINAL position, because that is what a PDO's Sm attribute
    // dereferences: Sm="3" above must resolve to "Inputs", the fourth <Sm> element.
    [Fact]
    public async Task Parse_numbers_sync_managers_by_their_ordinal_position()
    {
        var processData = await El3204ProcessDataAsync();

        processData.SyncManagers.Should().HaveCount(4);
        processData.SyncManagers.Select(s => s.Number).Should().Equal(new[] { 0, 1, 2, 3 });
        processData.SyncManagers[3].Name.Should().Be("Inputs");
        processData.SyncManagers[3].StartAddress.Should().Be(0x1180);
        processData.SyncManagers[3].ControlByte.Should().Be(0x20);
        processData.SyncManagers[3].DefaultSize.Should().Be(4);
        processData.SyncManagers[3].Enabled.Should().BeTrue();
        processData.SyncManagers[3].MinSize.Should().BeNull();
    }

    [Fact]
    public async Task Parse_reports_a_device_with_no_process_data_as_null()
    {
        var device = await EsiDeviceReader.TryReadAsync(El6xxx, new EsiKey(Beckhoff, El6001, Rev1));

        device.Should().NotBeNull();
        device!.ProcessData.Should().BeNull();
    }

    // #67: a device declaring an EMPTY map is not the same answer as one declaring none. EL6002
    // declares a sync manager and no PDOs.
    [Fact]
    public async Task Parse_distinguishes_an_empty_process_data_map_from_an_absent_one()
    {
        var device = await EsiDeviceReader.TryReadAsync(El6xxx, new EsiKey(Beckhoff, El6002, Rev1));

        device!.ProcessData.Should().NotBeNull();
        device.ProcessData!.Pdos.Should().BeEmpty();
        device.ProcessData.SyncManagers.Should().HaveCount(1);
    }
}
