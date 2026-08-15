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

    // <Object><Info><SubItem> is sparse and prefix-truncated: it lists only the sub-items
    // carrying default data. The fixture's 0x1018 lists 2 where DT1018 declares 3. This
    // particular truncation is a PREFIX ([SubIndex 000, Vendor ID] of [SubIndex 000, Vendor ID,
    // Product code]), so a positional join would pass this test too — it reads info[0]/info[1]
    // onto declared[0]/declared[1], which happen to be the same members the name join picks. The
    // fixture can't be changed to make this one discriminate: Beckhoff EL32xx.xml is frozen. See
    // Parse_joins_a_non_prefix_default_by_name_not_position below for the case a positional join
    // actually gets wrong.
    [Fact]
    public async Task Parse_joins_sub_item_default_data_by_name()
    {
        var dictionary = await El3204DictionaryAsync();

        dictionary.TryGetObject(0x1018, out EsiObject? identity).Should().BeTrue();
        identity!.SubItems[0].DefaultData.Should().Be("04");
        identity.SubItems[1].DefaultData.Should().Be("02000000");
        identity.SubItems[2].DefaultData.Should().BeNull();
    }

    // 0x1C12's <Info> names only "SubIndex 000" — the FIRST of DT1C12's two declared members —
    // so this, too, is a prefix a positional join gets right by luck: info[0] lands on
    // declared[0], and there is no declared[1] entry in <Info> to misplace. This test exists to
    // pin the shape (unmatched member kept, not dropped, default null), not to distinguish the
    // two joins. Parse_joins_a_non_prefix_default_by_name_not_position is the one that does.
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

    // The case a positional join actually fails: 0x1C13's <Info> names only "Beta", the LAST of
    // three declared members ([SubIndex 000, Alpha, Beta]) — not a prefix. A positional join
    // reads info[0] ("Beta"'s default) onto declared[0] ("SubIndex 000") and leaves declared[1]
    // and declared[2] without one, misattributing the default and losing it from where it
    // belongs. The name join must instead leave SubIndex 000 and Alpha untouched and give Beta
    // the default the file states for it.
    [Fact]
    public async Task Parse_joins_a_non_prefix_default_by_name_not_position()
    {
        var device = await EsiDeviceReader.TryReadAsync(El6xxx, new EsiKey(Beckhoff, El6001, Rev1));

        device!.ObjectDictionary!.TryGetObject(0x1C13, out EsiObject? assign).Should().BeTrue();
        assign!.SubItems.Should().HaveCount(3);
        assign.SubItems[0].Name.Should().Be("SubIndex 000");
        assign.SubItems[0].DefaultData.Should().BeNull();
        assign.SubItems[1].Name.Should().Be("Alpha");
        assign.SubItems[1].DefaultData.Should().BeNull();
        assign.SubItems[2].Name.Should().Be("Beta");
        assign.SubItems[2].DefaultData.Should().Be("03");
    }

    // The name join is by UNIQUE name — a name that is ambiguous on the declared side must not
    // receive a default even though <Info> names it once. 0x1C14's DT1C14 declares two sub-items
    // both named "Dup"; <Info> names "Dup" once with a default. Deleting the declared-count guard
    // from EsiDictionaryParser would let both "Dup" members claim it.
    [Fact]
    public async Task Parse_leaves_sub_items_without_default_data_when_the_declared_name_is_duplicated()
    {
        var device = await EsiDeviceReader.TryReadAsync(El6xxx, new EsiKey(Beckhoff, El6001, Rev1));

        device!.ObjectDictionary!.TryGetObject(0x1C14, out EsiObject? duplicate).Should().BeTrue();
        duplicate!.SubItems.Should().HaveCount(2);
        duplicate.SubItems[0].DefaultData.Should().BeNull();
        duplicate.SubItems[1].DefaultData.Should().BeNull();
    }

    // The mirror case: a name that is ambiguous on the <Info> side must not receive a default
    // even though it is unique among the declared members. 0x1C15's DT1C15 declares "Gamma"
    // once, but <Info> names "Gamma" twice with two different defaults. Deleting the info-count
    // guard from EsiDictionaryParser would let the later <Info><SubItem> silently win.
    [Fact]
    public async Task Parse_leaves_sub_items_without_default_data_when_the_info_name_is_duplicated()
    {
        var device = await EsiDeviceReader.TryReadAsync(El6xxx, new EsiKey(Beckhoff, El6001, Rev1));

        device!.ObjectDictionary!.TryGetObject(0x1C15, out EsiObject? duplicate).Should().BeTrue();
        duplicate!.SubItems.Should().HaveCount(2);
        duplicate.SubItems[0].Name.Should().Be("Gamma");
        duplicate.SubItems[0].DefaultData.Should().BeNull();
        duplicate.SubItems[1].Name.Should().Be("Delta");
        duplicate.SubItems[1].DefaultData.Should().BeNull();
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
