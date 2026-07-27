using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using TwinCAT.Ads;
using TwinCAT.TypeSystem;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Pins <c>AdsConnection.ReadValueWithMetadataAsync</c> end to end, without hardware, via the
/// internal <c>SetSymbolLoaderForTesting</c> seam — the same seam and the same
/// hardware-free reasoning as <see cref="ReadValuesAsyncPartitionTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is safe without a connected client.</b> A struct/function-block with sub-symbols
/// never touches <c>_client</c> at all: <see cref="PlcValueDecoder.DecodesFromSubSymbolsOnly"/>
/// makes <c>ReadValueWithMetadataAsync</c> skip its own top-level
/// <c>_client.ReadValueAsync</c> for such a symbol, exactly like
/// <c>AdsConnection.ReadValuesAsync</c>'s container branch. Every read below therefore goes
/// through <see cref="StubValueSymbol"/>'s member reads only, so these tests run
/// deterministically against an <see cref="AdsConnection"/> that is constructed but never
/// <c>Connect()</c>-ed.
/// </para>
/// <para>
/// <b>Consistency with the batch path.</b> The same struct symbol is read once through
/// <c>ReadValueWithMetadataAsync</c> and once through a one-symbol <c>ReadValuesAsync</c> batch;
/// both must produce the same decoded tree, <c>TypeName</c> and <c>Category</c> — proving the two
/// entry points share <see cref="PlcValueDecoder.DecodesFromSubSymbolsOnly"/> rather than each
/// re-deriving their own skip-the-read condition.
/// </para>
/// </remarks>
public class ReadValueWithMetadataAsyncTests
{
    private static AdsConnection CreateConnection(params ISymbol[] symbols)
    {
        var options = new PlcTargetOptions { DisplayName = "PLC 1", TimeoutMs = 3000 };
        var connection = new AdsConnection("plc1", options, new NullLoggerFactory());
        connection.SetSymbolLoaderForTesting(new FakeDynamicSymbolLoader(symbols));
        return connection;
    }

    [Fact]
    public async Task ReadValueWithMetadataAsync_StructWithSubSymbols_ReturnsDecodedTree_NotRawValue()
    {
        var motor = new StubValueSymbol("MAIN.Motor", DataTypeCategory.Struct, "ST_Motor", new object(),
            new StubValueSymbol("Speed", DataTypeCategory.Primitive, "INT", 1500),
            new StubValueSymbol("Running", DataTypeCategory.Primitive, "BOOL", true));

        using var connection = CreateConnection(motor);

        var result = await connection.ReadValueWithMetadataAsync("MAIN.Motor", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Succeeded);
        var tree = Assert.IsType<Dictionary<string, object?>>(result.Value);
        Assert.Equal(1500, tree["Speed"]);
        Assert.Equal(true, tree["Running"]);
        Assert.Equal("ST_Motor", result.TypeName);
        Assert.Equal("Struct", result.Category);
        Assert.Equal("MAIN.Motor", result.SymbolPath);
    }

    [Fact]
    public async Task ReadValueWithMetadataAsync_UnknownSymbol_ThrowsAdsErrorException_SymbolNotFound()
    {
        using var connection = CreateConnection(); // no symbols registered

        var ex = await Assert.ThrowsAsync<AdsErrorException>(
            () => connection.ReadValueWithMetadataAsync("MAIN.NoSuchSymbol", CancellationToken.None));

        Assert.Equal(AdsErrorCode.DeviceSymbolNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task ReadValueWithMetadataAsync_MemberReadFails_SurfacesAsAdsErrorException()
    {
        var motor = new StubValueSymbol("MAIN.Motor", DataTypeCategory.Struct, "ST_Motor", new object(),
            StubValueSymbol.ThatFailsToRead("Speed", DataTypeCategory.Primitive, "INT"));

        using var connection = CreateConnection(motor);

        var ex = await Assert.ThrowsAsync<AdsErrorException>(
            () => connection.ReadValueWithMetadataAsync("MAIN.Motor", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(AdsErrorCode.DeviceError, ex.ErrorCode);
    }

    [Fact]
    public async Task ReadValueWithMetadataAsync_SameStructSingleAndBatch_ProduceIdenticalShape()
    {
        // Two independent StubValueSymbol instances for the same logical struct, one per
        // connection, so the single-read and batch-read paths cannot share any mutable state —
        // any divergence in the assertions below can only come from the two entry points
        // genuinely decoding differently, not from incidental sharing.
        ISymbol MakeMotor() => new StubValueSymbol("MAIN.Motor", DataTypeCategory.Struct, "ST_Motor", new object(),
            new StubValueSymbol("Speed", DataTypeCategory.Primitive, "INT", 1500),
            new StubValueSymbol("Running", DataTypeCategory.Primitive, "BOOL", true));

        using var singleConnection = CreateConnection(MakeMotor());
        using var batchConnection = CreateConnection(MakeMotor());

        var singleResult = await singleConnection.ReadValueWithMetadataAsync("MAIN.Motor", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var batchResults = await batchConnection.ReadValuesAsync(["MAIN.Motor"], CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var batchResult = batchResults["MAIN.Motor"];

        Assert.Equal(batchResult.Succeeded, singleResult.Succeeded);
        Assert.Equal(batchResult.TypeName, singleResult.TypeName);
        Assert.Equal(batchResult.Category, singleResult.Category);
        Assert.Equal(
            Assert.IsType<Dictionary<string, object?>>(batchResult.Value),
            Assert.IsType<Dictionary<string, object?>>(singleResult.Value));
    }
}
