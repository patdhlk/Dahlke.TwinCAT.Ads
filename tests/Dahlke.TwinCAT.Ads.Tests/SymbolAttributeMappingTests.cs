using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Dahlke.TwinCAT.Ads.Tests;

public class SymbolAttributeMappingTests
{
    // Mirrors the (name, value) pair shape of ITypeAttribute without needing a live symbol.
    private static IReadOnlyDictionary<string, string> Merge(
        IEnumerable<(string Name, string Value)> typeAttributes,
        IEnumerable<(string Name, string Value)> instanceAttributes)
        => AdsConnection.MergeAttributeSources(typeAttributes, instanceAttributes);

    [Fact]
    public void Type_attributes_apply_to_every_instance()
    {
        var merged = Merge([("OPC.UA.DA", "1")], []);

        Assert.Equal("1", merged["OPC.UA.DA"]);
    }

    [Fact]
    public void Instance_attributes_win_over_type_attributes()
    {
        var merged = Merge([("OPC.UA.DA", "1")], [("OPC.UA.DA", "0")]);

        Assert.Equal("0", merged["OPC.UA.DA"]);
    }

    [Fact]
    public void Keys_are_matched_case_insensitively()
    {
        var merged = Merge([("opc.ua.da", "1")], []);

        Assert.Equal("1", merged["OPC.UA.DA"]);
    }

    [Fact]
    public void No_attributes_yields_an_empty_dictionary_not_null()
    {
        var merged = Merge([], []);

        Assert.NotNull(merged);
        Assert.Empty(merged);
    }
}
