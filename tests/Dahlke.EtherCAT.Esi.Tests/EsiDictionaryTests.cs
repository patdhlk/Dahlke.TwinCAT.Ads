using FluentAssertions;

namespace Dahlke.EtherCAT.Esi.Tests;

public class EsiDictionaryTests
{
    private static EsiObject Object(ushort index, string name) =>
        new(
            Index: index,
            Name: name,
            DataType: null,
            BitSize: null,
            Access: null,
            AccessRaw: null,
            WriteRestrictions: null,
            DefaultData: null,
            DefaultValue: null,
            SubItems: []);

    [Fact]
    public void TryGetObject_finds_an_object_by_index()
    {
        var dictionary = new EsiObjectDictionary([Object(0x1000, "Device type"), Object(0x6000, "RTD Inputs")]);

        dictionary.TryGetObject(0x6000, out EsiObject? found).Should().BeTrue();
        found!.Name.Should().Be("RTD Inputs");
    }

    [Fact]
    public void TryGetObject_reports_an_index_the_device_does_not_declare()
    {
        var dictionary = new EsiObjectDictionary([Object(0x1000, "Device type")]);

        dictionary.TryGetObject(0x7000, out EsiObject? found).Should().BeFalse();
        found.Should().BeNull();
    }

    // A duplicated index is malformed ESI. Keeping the first and carrying on beats throwing:
    // one bad object must not cost a caller the whole device, which is the same reasoning
    // EsiCatalog applies to one bad file in a folder.
    [Fact]
    public void TryGetObject_keeps_the_first_of_a_duplicated_index()
    {
        var dictionary = new EsiObjectDictionary([Object(0x1000, "first"), Object(0x1000, "second")]);

        dictionary.TryGetObject(0x1000, out EsiObject? found).Should().BeTrue();
        found!.Name.Should().Be("first");
    }

    private const uint Beckhoff = 2;
    private const uint El3204 = 0x0C843052;
    private const uint El3205 = 0x0C853052;
    private const uint Rev1 = 0x00100000;

    private static string El32xx =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi", "Beckhoff EL32xx.xml");

    private static async Task<EsiObjectDictionary> El3204DictionaryAsync()
    {
        var device = await EsiDeviceReader.TryReadAsync(El32xx, new EsiKey(Beckhoff, El3204, Rev1));

        device.Should().NotBeNull();
        device!.ObjectDictionary.Should().NotBeNull();

        return device.ObjectDictionary!;
    }

    [Fact]
    public async Task Parse_reports_every_field_of_a_flat_object()
    {
        var dictionary = await El3204DictionaryAsync();

        dictionary.TryGetObject(0x1000, out EsiObject? found).Should().BeTrue();
        found!.Name.Should().Be("Device type");
        found.DataType.Should().Be("UDINT");
        found.BitSize.Should().Be(32);
        found.Access.Should().Be(EsiAccess.ReadOnly);
        found.AccessRaw.Should().Be("ro");
        found.DefaultData.Should().Be("89134001");
        found.SubItems.Should().BeEmpty();
    }

    [Fact]
    public async Task Parse_reports_write_restrictions_and_the_textual_default()
    {
        var dictionary = await El3204DictionaryAsync();

        dictionary.TryGetObject(0x8000, out EsiObject? found).Should().BeTrue();
        found!.Access.Should().Be(EsiAccess.ReadWrite);
        found.WriteRestrictions.Should().Be("PreOP");
        found.DefaultValue.Should().Be("0");
        found.DefaultData.Should().BeNull();
    }

    [Fact]
    public async Task Parse_lists_objects_in_the_order_the_file_declares_them()
    {
        var dictionary = await El3204DictionaryAsync();

        // An explicit ushort[] rather than a collection expression: [0x1000, ...] would be
        // ambiguous between Equal(IEnumerable<T>) and Equal(params T[]).
        dictionary.Objects.Select(o => o.Index)
            .Should().Equal(new ushort[] { 0x1000, 0x8000, 0x1018 });
    }

    // #66: a device declaring no dictionary must be distinguishable from one declaring an empty
    // one. This is the "declares none" half; the empty half is pinned separately.
    [Fact]
    public async Task Parse_reports_a_device_with_no_dictionary_as_null()
    {
        var device = await EsiDeviceReader.TryReadAsync(El32xx, new EsiKey(Beckhoff, El3205, Rev1));

        device.Should().NotBeNull();
        device!.ObjectDictionary.Should().BeNull();
    }

    private const uint El6001 = 0x17CD3052;
    private const uint El6002 = 0x17CE3052;

    private static string El6xxx =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "EsiDetail", "Beckhoff EL6xxx.xml");

    // #66's core requirement: record sub-items are NESTED under their parent, not flattened, and
    // carry the sub-index a consumer needs to render 0xIIII:SS.
    [Fact]
    public async Task Parse_nests_record_sub_items_under_their_parent_object()
    {
        var dictionary = await El3204DictionaryAsync();

        dictionary.TryGetObject(0x1018, out EsiObject? identity).Should().BeTrue();
        identity!.SubItems.Should().HaveCount(3);
        identity.SubItems.Select(s => s.SubIndex).Should().Equal(new byte?[] { 0, 1, 2 });
        identity.SubItems[1].Name.Should().Be("Vendor ID");
        identity.SubItems[1].DataType.Should().Be("UDINT");
        identity.SubItems[1].BitSize.Should().Be(32);
        identity.SubItems[1].BitOffset.Should().Be(16);
        identity.SubItems[1].Access.Should().Be(EsiAccess.ReadOnly);
    }

    // The trap #66 does not mention. <Object><Info><SubItem> is sparse and prefix-truncated: it
    // lists only the sub-items carrying default data. The fixture's 0x1018 lists 2 where DT1018
    // declares 3. Joining by NAME keeps the defaults on the right members; joining by position
    // would too, here — the next test is the one a positional join fails.
    [Fact]
    public async Task Parse_joins_sub_item_default_data_by_name()
    {
        var dictionary = await El3204DictionaryAsync();

        dictionary.TryGetObject(0x1018, out EsiObject? identity).Should().BeTrue();
        identity!.SubItems[0].DefaultData.Should().Be("04");
        identity.SubItems[1].DefaultData.Should().Be("02000000");
        identity.SubItems[2].DefaultData.Should().BeNull();
    }

    // A positional join would put "SubIndex 000"'s default data ("02") onto whichever member
    // happens to sit at index 0 of the DATATYPE's list — correct here by luck. This fixture's
    // truncation is on the SECOND member, so the assertion that "Elements" carries no default is
    // what a positional join gets wrong once the lists differ in length in a real way.
    [Fact]
    public async Task Parse_leaves_an_unmatched_sub_item_without_default_data()
    {
        var device = await EsiDeviceReader.TryReadAsync(El6xxx, new EsiKey(Beckhoff, El6001, Rev1));

        device!.ObjectDictionary!.TryGetObject(0x1C12, out EsiObject? assign).Should().BeTrue();
        assign!.SubItems.Should().HaveCount(2);
        assign.SubItems[0].Name.Should().Be("SubIndex 000");
        assign.SubItems[0].DefaultData.Should().Be("02");
        assign.SubItems[1].Name.Should().Be("Elements");
        assign.SubItems[1].DefaultData.Should().BeNull();
    }

    // 19,967 of 637,647 sub-items in Beckhoff's set omit <SubIdx> because they are array members
    // whose indices are implied by <ArrayInfo>. Deriving one would be inference, so absence is
    // reported as absence.
    [Fact]
    public async Task Parse_reports_an_array_sub_item_without_a_sub_index_as_null()
    {
        var device = await EsiDeviceReader.TryReadAsync(El6xxx, new EsiKey(Beckhoff, El6001, Rev1));

        device!.ObjectDictionary!.TryGetObject(0x1C12, out EsiObject? assign).Should().BeTrue();
        assign!.SubItems[0].SubIndex.Should().Be(0);
        assign.SubItems[1].SubIndex.Should().BeNull();
    }

    // #66: a declared-but-empty dictionary is NOT the same answer as no dictionary at all.
    [Fact]
    public async Task Parse_distinguishes_an_empty_dictionary_from_an_absent_one()
    {
        var device = await EsiDeviceReader.TryReadAsync(El6xxx, new EsiKey(Beckhoff, El6002, Rev1));

        device!.ObjectDictionary.Should().NotBeNull();
        device.ObjectDictionary!.Objects.Should().BeEmpty();
    }
}
