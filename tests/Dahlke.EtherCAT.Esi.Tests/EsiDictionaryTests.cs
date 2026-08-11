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
}
