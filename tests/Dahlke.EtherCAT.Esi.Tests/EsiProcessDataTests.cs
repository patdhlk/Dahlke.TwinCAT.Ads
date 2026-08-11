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
    // consumer to infer from a string. The fixture carries two <RxPdo> (0x1600, 0x1601) and one
    // <TxPdo> (0x1A00), so this also confirms grouping copes with more than one PDO per direction.
    [Fact]
    public async Task Parse_groups_pdos_by_direction_taken_from_the_element_name()
    {
        var processData = await El3204ProcessDataAsync();

        processData.Pdos.Should().HaveCount(3);
        processData.Pdos.Single(p => p.Direction == EsiPdoDirection.Transmit).Index.Should().Be(0x1A00);
        processData.Pdos.Where(p => p.Direction == EsiPdoDirection.Receive)
            .Select(p => p.Index).Should().BeEquivalentTo(new ushort[] { 0x1600, 0x1601 });
    }

    // The fixture lists Rx, Tx, Rx (0x1600, 0x1A00, 0x1601) — deliberately not one PDO per
    // direction. Grouping ALL Tx then ALL Rx, or ALL Rx then ALL Tx, both disagree with this order,
    // so unlike a fixture with a single PDO per direction, no grouping strategy survives it: only
    // the file's own document order does.
    [Fact]
    public async Task Parse_reports_pdos_in_the_files_own_document_order()
    {
        var processData = await El3204ProcessDataAsync();

        processData.Pdos.Should().HaveCount(3);
        processData.Pdos[0].Direction.Should().Be(EsiPdoDirection.Receive);
        processData.Pdos[0].Index.Should().Be(0x1600);
        processData.Pdos[1].Direction.Should().Be(EsiPdoDirection.Transmit);
        processData.Pdos[1].Index.Should().Be(0x1A00);
        processData.Pdos[2].Direction.Should().Be(EsiPdoDirection.Receive);
        processData.Pdos[2].Index.Should().Be(0x1601);
    }

    [Fact]
    public async Task Parse_reports_every_entry_field()
    {
        var processData = await El3204ProcessDataAsync();

        EsiPdo tx = processData.Pdos.Single(p => p.Direction == EsiPdoDirection.Transmit);
        tx.Name.Should().Be("RTD Inputs");
        tx.Fixed.Should().BeTrue();
        tx.Mandatory.Should().BeTrue();

        // The one entry in this PDO that carries a <Comment>.
        EsiPdoEntry underrange = tx.Entries[0];
        underrange.Comment.Should().Be("Underrange event active");

        EsiPdoEntry value = tx.Entries[2];
        value.Index.Should().Be(0x6000);
        value.SubIndex.Should().Be(17);
        value.BitLength.Should().Be(16);
        value.Name.Should().Be("Value");
        value.DataType.Should().Be("INT");
        value.Comment.Should().BeNull();
    }

    // A padding entry is <Index>#x0</Index> with a bit length and nothing else. Dropping it would
    // silently corrupt any consumer computing bit offsets down the PDO, so it is kept with its
    // four absent fields reported as null.
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
        padding.Comment.Should().BeNull();
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
    // majority case, not an edge case. Targeted by Index, not Direction: the fixture now carries
    // two <RxPdo> (0x1600, 0x1601), so Direction alone no longer picks one.
    [Fact]
    public async Task Parse_reports_a_pdo_with_no_sm_attribute_as_unassigned()
    {
        var processData = await El3204ProcessDataAsync();

        processData.Pdos.Single(p => p.Index == 0x1600)
            .SyncManager.Should().BeNull();
    }

    // The fixture's 0x1600 RxPdo carries Fixed="1" but no Mandatory attribute at all, so the
    // nullable must come back null rather than defaulting to false.
    [Fact]
    public async Task Parse_reports_no_mandatory_attribute_as_null()
    {
        var processData = await El3204ProcessDataAsync();

        EsiPdo rx = processData.Pdos.Single(p => p.Index == 0x1600);
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

        // "Outputs" declares DefaultSize="0" — a genuine zero, not the absence a null would mean.
        // This is the attribute-level twin of Parse_keeps_padding_entries_rather_than_dropping_them's
        // element-level zero-vs-null pin.
        processData.SyncManagers[2].DefaultSize.Should().Be(0);
    }

    // "MBoxOut" declares distinct MinSize and MaxSize values. Asserting only one of the two, or
    // asserting them equal, would still pass if the parser swapped them; 3,222 of 7,222 real sync
    // managers in Beckhoff's published set declare MinSize != MaxSize.
    [Fact]
    public async Task Parse_does_not_swap_min_and_max_size()
    {
        var processData = await El3204ProcessDataAsync();

        EsiSyncManager mboxOut = processData.SyncManagers[0];
        mboxOut.Name.Should().Be("MBoxOut");
        mboxOut.MinSize.Should().Be(34);
        mboxOut.MaxSize.Should().Be(128);
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
