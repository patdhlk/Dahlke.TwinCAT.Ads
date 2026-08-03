using Microsoft.Extensions.Logging.Abstractions;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Pins the simulation counterpart of <see cref="AdsSymbolInfo.Attributes"/>: a
/// <see cref="ConnectionMode.Simulated"/> target seeded via
/// <see cref="PlcTargetOptions.SymbolAttributes"/> reports those pragmas from
/// <see cref="SimulatedAdsConnection.GetSymbolTreeAsync"/>, and an unseeded symbol reports an
/// empty attribute set rather than <see langword="null"/> — the same contract
/// <see cref="AdsConnection"/> upholds against a live PLC.
/// </summary>
public class SimulatedSymbolAttributeTests
{
    private static PlcTargetOptions Target() => new()
    {
        AmsNetId = "127.0.0.1.1.1",
        Port = 851,
        Mode = ConnectionMode.Simulated,
        InitialValues = new Dictionary<string, object?>
        {
            ["MAIN.nCounter"] = 0,
            ["MAIN.nHidden"] = 0,
        },
        SymbolAttributes = new Dictionary<string, Dictionary<string, string>>
        {
            ["MAIN.nCounter"] = new() { ["OPC.UA.DA"] = "1" },
        },
    };

    // AdsConnectionPoolSimulatedTests.cs constructs SimulatedAdsConnection directly and seeds it
    // via its Set* methods rather than through a static factory — there is no
    // SimulatedAdsConnection.Create entry point in this codebase. Matching that established
    // pattern here rather than inventing one.
    private static SimulatedAdsConnection CreateConnection(PlcTargetOptions options)
    {
        var connection = new SimulatedAdsConnection("plc1", options.DisplayName, NullLoggerFactory.Instance);
        connection.SetInitialValues(options.InitialValues);
        connection.SetSymbolAttributes(options.SymbolAttributes);
        return connection;
    }

    [Fact]
    public async Task Seeded_attributes_appear_on_the_simulated_symbol()
    {
        var connection = CreateConnection(Target());

        var symbols = await connection.GetSymbolTreeAsync("MAIN");
        var counter = symbols.Single(s => s.InstancePath == "MAIN.nCounter");

        Assert.Equal("1", counter.Attributes!["OPC.UA.DA"]);
    }

    [Fact]
    public async Task Unseeded_symbols_report_empty_not_null_attributes()
    {
        var connection = CreateConnection(Target());

        var symbols = await connection.GetSymbolTreeAsync("MAIN");
        var hidden = symbols.Single(s => s.InstancePath == "MAIN.nHidden");

        Assert.NotNull(hidden.Attributes);
        Assert.Empty(hidden.Attributes!);
    }
}
