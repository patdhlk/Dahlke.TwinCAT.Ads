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
}
