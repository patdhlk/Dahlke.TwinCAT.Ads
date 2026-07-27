using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using TwinCAT.TypeSystem;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Pins <c>AdsConnection.ReadValuesAsync</c>'s batch-read partition end to end, without
/// hardware, via the internal <c>SetSymbolLoaderForTesting</c> seam.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is safe without a connected client.</b> <c>SumSymbolRead</c> construction lives
/// entirely inside <c>ReadValuesAsync</c>'s <c>scalars.Count &gt; 0</c> branch, and
/// <see cref="PlcValueDecoder.DecodesFromSubSymbolsOnly"/> means <c>AdsConnection</c> skips its
/// own top-level <c>_client.ReadValueAsync</c> for structs/function-blocks that expose
/// sub-symbols too. A batch made up entirely of such symbols therefore never touches
/// <c>_client</c> at any point, so these tests run deterministically against an
/// <see cref="AdsConnection"/> that is constructed but never <c>Connect()</c>-ed.
/// </para>
/// <para>
/// <b>Prior to this task's fix round, this class did not exist</b> — the container branch of
/// <c>ReadValuesAsync</c> had no executable test at all (see Task 4's review Finding 2):
/// striking the branch and routing structs through the sum command left the full suite green.
/// </para>
/// </remarks>
public class ReadValuesAsyncPartitionTests
{
    private static AdsConnection CreateConnection(params ISymbol[] symbols)
    {
        var options = new PlcTargetOptions { DisplayName = "PLC 1", TimeoutMs = 3000 };
        var connection = new AdsConnection("plc1", options, new NullLoggerFactory());
        connection.SetSymbolLoaderForTesting(new FakeDynamicSymbolLoader(symbols));
        return connection;
    }

    [Fact]
    public async Task ReadValuesAsync_StructInBatch_ReturnsDecodedTree_NotRawValue()
    {
        var motor = new StubValueSymbol("MAIN.Motor", DataTypeCategory.Struct, "ST_Motor", new object(),
            new StubValueSymbol("Speed", DataTypeCategory.Primitive, "INT", 1500),
            new StubValueSymbol("Running", DataTypeCategory.Primitive, "BOOL", true));

        using var connection = CreateConnection(motor);

        var results = await connection.ReadValuesAsync(["MAIN.Motor"], CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(results["MAIN.Motor"].Succeeded);
        var tree = Assert.IsType<Dictionary<string, object?>>(results["MAIN.Motor"].Value);
        Assert.Equal(1500, tree["Speed"]);
        Assert.Equal(true, tree["Running"]);
        Assert.Equal("ST_Motor", results["MAIN.Motor"].TypeName);
        Assert.Equal("Struct", results["MAIN.Motor"].Category);
    }

    [Fact]
    public async Task ReadValuesAsync_ContainerOnlyBatch_IssuesNoSumCommand()
    {
        // A batch of two struct-with-subsymbols paths, no scalars at all. If ReadValuesAsync's
        // container branch were struck and structs rerouted through the sum command (the
        // regression this task exists to prevent), this call would need a connected _client —
        // but `connection` here is never connected, so a wrong reroute would either throw
        // (disconnected client) or hang. The WaitAsync bound turns "would hang" into a failing
        // test rather than a stuck test run; a passing result within the bound is only possible
        // via the container path, which never touches _client for these symbols.
        var motor = new StubValueSymbol("MAIN.Motor", DataTypeCategory.Struct, "ST_Motor", new object(),
            new StubValueSymbol("Speed", DataTypeCategory.Primitive, "INT", 1500));
        var axis = new StubValueSymbol("MAIN.Axis", DataTypeCategory.FunctionBlock, "FB_Axis", new object(),
            new StubValueSymbol("Position", DataTypeCategory.Primitive, "LREAL", 3.5));

        using var connection = CreateConnection(motor, axis);

        var results = await connection.ReadValuesAsync(["MAIN.Motor", "MAIN.Axis"], CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(results["MAIN.Motor"].Succeeded);
        Assert.IsType<Dictionary<string, object?>>(results["MAIN.Motor"].Value);
        Assert.True(results["MAIN.Axis"].Succeeded);
        var axisTree = Assert.IsType<Dictionary<string, object?>>(results["MAIN.Axis"].Value);
        Assert.Equal(3.5, axisTree["Position"]);
    }

    [Theory]
    [InlineData(DataTypeCategory.Primitive, false)]
    [InlineData(DataTypeCategory.String, false)]
    [InlineData(DataTypeCategory.Enum, false)]
    [InlineData(DataTypeCategory.Struct, true)]
    [InlineData(DataTypeCategory.FunctionBlock, true)]
    [InlineData(DataTypeCategory.Array, true)]
    public void IsContainer_RoutesStructFunctionBlockArrayToContainerPath_EverythingElseToSumPath(
        DataTypeCategory category, bool expectedContainer)
    {
        // AdsConnection.ReadValuesAsync partitions with exactly this predicate. Proving the sum
        // path is actually taken for non-containers requires a live ADS round-trip (out of
        // reach without hardware — see class remarks), so this pins the ROUTING DECISION itself:
        // a pure, hardware-free classification pinned the same way SumResultMapperTests and
        // PlcValueDecoderTests already pin their own pure logic.
        var symbol = new StubSymbol(category, "SomeType");

        Assert.Equal(expectedContainer, AdsConnection.IsContainer(symbol));
    }
}
