using System.Collections.Generic;
using Dahlke.TwinCAT.Ads;
using Xunit;

namespace Dahlke.TwinCAT.Ads.Tests;

public class AdsSymbolInfoAttributeTests
{
    [Fact]
    public void Attributes_default_to_null_meaning_not_collected()
    {
        var symbol = new AdsSymbolInfo("MAIN.n", "INT", "Primitive", 2, null, null);

        Assert.Null(symbol.Attributes);
    }

    [Fact]
    public void Attributes_round_trip_when_set()
    {
        var symbol = new AdsSymbolInfo("MAIN.n", "INT", "Primitive", 2, null, null)
        {
            Attributes = new Dictionary<string, string> { ["OPC.UA.DA"] = "1" },
        };

        Assert.Equal("1", symbol.Attributes!["OPC.UA.DA"]);
    }

    [Fact]
    public void Empty_attributes_are_distinct_from_null()
    {
        var symbol = new AdsSymbolInfo("MAIN.n", "INT", "Primitive", 2, null, null)
        {
            Attributes = new Dictionary<string, string>(),
        };

        Assert.NotNull(symbol.Attributes);
        Assert.Empty(symbol.Attributes!);
    }
}
