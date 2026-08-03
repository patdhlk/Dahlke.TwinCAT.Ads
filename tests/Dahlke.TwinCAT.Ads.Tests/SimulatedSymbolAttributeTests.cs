using Microsoft.Extensions.Logging.Abstractions;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Pins the simulation counterpart of <see cref="AdsSymbolInfo.Attributes"/>: a
/// <see cref="ConnectionMode.Simulated"/> target seeded via
/// <see cref="PlcTargetOptions.SymbolAttributes"/> reports those pragmas from
/// <see cref="SimulatedAdsConnection.GetSymbolTreeAsync"/>, and an unseeded symbol reports an
/// empty attribute set rather than <see langword="null"/> — the same contract
/// <see cref="AdsConnection"/> upholds against a live PLC. Also pins the two global
/// constraints on <see cref="PlcTargetOptions.SymbolAttributes"/> (both the outer
/// symbol-path key and the inner attribute name compare <see cref="StringComparer.OrdinalIgnoreCase"/>)
/// and the production wiring seam — <see cref="AdsConnectionFactory"/> —
/// that a later consumer configuring a target through
/// <see cref="AdsConnectionPoolBuilder"/> actually goes through, as opposed to the direct
/// <see cref="SimulatedAdsConnection"/> construction the other facts here use.
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

    [Fact]
    public async Task Seeded_attributes_match_the_symbol_path_case_insensitively()
    {
        // The seeded SymbolAttributes key is cased DIFFERENTLY than the seeded value's path
        // (and than what the tree reports back). Both PlcTargetOptions.SymbolAttributes'
        // own remarks and this task's global constraints state the outer symbol-path key
        // compares case-insensitively — this proves it is not merely true by coincidence of
        // every other fact in this file agreeing on casing.
        var options = new PlcTargetOptions
        {
            Mode = ConnectionMode.Simulated,
            InitialValues = new Dictionary<string, object?> { ["MAIN.nCounter"] = 0 },
            SymbolAttributes = new Dictionary<string, Dictionary<string, string>>
            {
                ["main.ncounter"] = new() { ["OPC.UA.DA"] = "1" },
            },
        };
        var connection = CreateConnection(options);

        var symbols = await connection.GetSymbolTreeAsync("MAIN");
        var counter = symbols.Single(s => s.InstancePath == "MAIN.nCounter");

        Assert.Equal("1", counter.Attributes!["OPC.UA.DA"]);
    }

    [Fact]
    public async Task Seeded_attribute_names_are_matched_case_insensitively()
    {
        // Same point as above, for the INNER attribute-name key: seeded as "OPC.UA.DA",
        // looked up with different casing.
        var connection = CreateConnection(Target());

        var symbols = await connection.GetSymbolTreeAsync("MAIN");
        var counter = symbols.Single(s => s.InstancePath == "MAIN.nCounter");

        Assert.Equal("1", counter.Attributes!["opc.ua.da"]);
    }

    [Fact]
    public async Task SymbolAttributes_reaches_the_simulated_tree_through_the_real_dispatch_path()
    {
        // Every other fact in this file constructs SimulatedAdsConnection directly and seeds
        // it by hand — that bypasses AdsConnectionFactory.Create, the one production call
        // site that wires options.SymbolAttributes into a simulated connection for a
        // consumer going through AdsConnectionPoolBuilder / AddTwinCatAdsSimulation (which is
        // how a later, hardware-free consumer will actually configure a target). This proves
        // the seam itself, end to end.
        await using var pool = await AdsConnectionPoolBuilder.CreateSimulation()
            .AddTarget("plc1", o =>
            {
                o.DisplayName = "Simulated PLC";
                o.InitialValues["MAIN.nCounter"] = 0;
                o.SymbolAttributes["MAIN.nCounter"] = new() { ["OPC.UA.DA"] = "1" };
            })
            .BuildAndStartAsync();

        var conn = pool.GetConnection("plc1");
        var symbols = await conn.GetSymbolTreeAsync("MAIN");
        var counter = symbols.Single(s => s.InstancePath == "MAIN.nCounter");

        Assert.Equal("1", counter.Attributes!["OPC.UA.DA"]);
    }
}
