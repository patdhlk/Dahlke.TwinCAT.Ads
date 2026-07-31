using Microsoft.Extensions.Logging.Abstractions;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Unit tests for the simulated RPC surface. The real path is covered by the contract
/// suite and, for the shape that only hardware can prove, by the hardware tests.
/// </summary>
public class AdsRpcTests
{
    private static SimulatedAdsConnection NewSim() =>
        new("plc1", "PLC One", NullLoggerFactory.Instance);

    [Fact]
    public async Task SeededHandler_ReceivesTheArguments_AndItsResultIsReturned()
    {
        var sim = NewSim();
        object?[]? seen = null;
        sim.SetRpcHandler("MAIN.Fb", "DoIt", args => { seen = args; return new AdsRpcResult(42, []); });

        var result = await sim.InvokeRpcMethodAsync("MAIN.Fb", "DoIt", ["abc"], CancellationToken.None);

        Assert.Equal(42, result.ReturnValue);
        Assert.Equal(["abc"], seen);
    }

    [Fact]
    public async Task Lookup_IsCaseInsensitive_OnPathAndMethod()
    {
        var sim = NewSim();
        sim.SetRpcHandler("MAIN.Fb", "DoIt", _ => new AdsRpcResult(1, []));

        var result = await sim.InvokeRpcMethodAsync("main.fb", "doit", [], CancellationToken.None);

        Assert.Equal(1, result.ReturnValue);
    }

    [Fact]
    public async Task UnseededCall_Throws_NamingPathAndMethod()
    {
        // A simulated RPC that silently returned null would let a simulated acknowledge
        // appear to succeed while doing nothing — the exact defect this whole change exists
        // to remove. It must be impossible to reproduce in the test double.
        var sim = NewSim();

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => sim.InvokeRpcMethodAsync("MAIN.Fb", "DoIt", [], CancellationToken.None));

        Assert.Contains("MAIN.Fb", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DoIt", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every other test here seeds and calls the SAME <c>("MAIN.Fb", "DoIt")</c> pair, so an
    /// implementation keying on the path alone, or on the method alone, passes all of them. This
    /// one seeds <c>A.Fb/DoIt</c> and then varies exactly one half of the key at a time: a
    /// path-only lookup would answer <c>A.Fb/Other</c>, a method-only lookup would answer
    /// <c>B.Fb/DoIt</c>. Both must miss.
    /// </summary>
    [Theory]
    [InlineData("A.Fb", "Other")]  // right path, wrong method — fails a path-only key
    [InlineData("B.Fb", "DoIt")]   // wrong path, right method — fails a method-only key
    public async Task Lookup_UsesBothHalvesOfTheKey(string callPath, string callMethod)
    {
        var sim = NewSim();
        sim.SetRpcHandler("A.Fb", "DoIt", _ => new AdsRpcResult(1, []));

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => sim.InvokeRpcMethodAsync(callPath, callMethod, [], CancellationToken.None));

        Assert.Contains(callPath, ex.Message, StringComparison.Ordinal);
        Assert.Contains(callMethod, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeededPair_IsStillReachable_AfterTheNearMisses()
    {
        // Guards the theory above from passing for the wrong reason: a lookup broken outright
        // would also throw for A.Fb/DoIt, and the theory alone could not tell that apart.
        var sim = NewSim();
        sim.SetRpcHandler("A.Fb", "DoIt", _ => new AdsRpcResult(1, []));

        var result = await sim.InvokeRpcMethodAsync("A.Fb", "DoIt", [], CancellationToken.None);

        Assert.Equal(1, result.ReturnValue);
    }

    [Fact]
    public async Task OutParameters_AreCarriedThrough()
    {
        var sim = NewSim();
        sim.SetRpcHandler("MAIN.Fb", "DoIt", _ => new AdsRpcResult(null, [1, "two"]));

        var result = await sim.InvokeRpcMethodAsync("MAIN.Fb", "DoIt", [], CancellationToken.None);

        Assert.Equal(2, result.OutParameters.Count);
        Assert.Equal("two", result.OutParameters[1]);
    }

    [Fact]
    public async Task CancelledToken_Throws()
    {
        var sim = NewSim();
        sim.SetRpcHandler("MAIN.Fb", "DoIt", _ => new AdsRpcResult(1, []));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sim.InvokeRpcMethodAsync("MAIN.Fb", "DoIt", [], cts.Token));
    }
}
