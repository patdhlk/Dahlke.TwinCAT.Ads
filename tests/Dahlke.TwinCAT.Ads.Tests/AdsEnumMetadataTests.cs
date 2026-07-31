using Microsoft.Extensions.Logging.Abstractions;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>Unit tests for the simulated enum-metadata surface.</summary>
public class AdsEnumMetadataTests
{
    private static SimulatedAdsConnection NewSim() =>
        new("plc1", "PLC One", NullLoggerFactory.Instance);

    [Fact]
    public async Task SeededMembers_AreReturned()
    {
        var sim = NewSim();
        sim.SetEnumMembers("deaReturnType",
            [new AdsEnumMember("SUCCESS", 0), new AdsEnumMember("ERROR", 1)]);

        var members = await sim.GetEnumMembersAsync("deaReturnType", CancellationToken.None);

        Assert.Equal(2, members.Count);
        Assert.Equal("SUCCESS", members[0].Name);
        Assert.Equal(0, members[0].Value);
    }

    [Fact]
    public async Task TypeNameLookup_IsCaseInsensitive()
    {
        var sim = NewSim();
        sim.SetEnumMembers("deaReturnType", [new AdsEnumMember("SUCCESS", 0)]);

        var members = await sim.GetEnumMembersAsync("DEARETURNTYPE", CancellationToken.None);

        Assert.Single(members);
    }

    [Fact]
    public async Task UnseededType_Throws_NamingTheType()
    {
        var sim = NewSim();

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => sim.GetEnumMembersAsync("deaReturnType", CancellationToken.None));

        Assert.Contains("deaReturnType", ex.Message, StringComparison.Ordinal);
    }
}
