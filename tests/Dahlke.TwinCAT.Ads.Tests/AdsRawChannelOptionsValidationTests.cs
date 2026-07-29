namespace Dahlke.TwinCAT.Ads.Tests;

public class AdsRawChannelOptionsValidationTests
{
    private static ValidateOptionsResultShim Validate(Action<AdsRawChannelOptions> configure)
    {
        var options = new TwinCatAdsOptions();
        // ValidateTargets requires at least one PLC target regardless of
        // RawChannels; a Simulated dummy target satisfies that rule without
        // needing an AmsNetId, so only RawChannels validation is under test.
        options.Targets["dummy"] = new PlcTargetOptions { Mode = ConnectionMode.Simulated };
        configure(options.RawChannels);
        var result = new TwinCatAdsOptionsValidator().Validate(null, options);
        return new ValidateOptionsResultShim(result.Failed, result.Failures ?? []);
    }

    private sealed record ValidateOptionsResultShim(bool Failed, IEnumerable<string> Failures);

    [Fact]
    public void Defaults_AreValid()
    {
        var result = Validate(_ => { });
        Assert.False(result.Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TimeoutMs_MustBePositive(int value)
    {
        var result = Validate(o => o.TimeoutMs = value);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("RawChannels:TimeoutMs"));
    }

    [Fact]
    public void RetryCount_MayBeZero_ButNotNegative()
    {
        Assert.False(Validate(o => o.RetryCount = 0).Failed);

        var result = Validate(o => o.RetryCount = -1);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("RawChannels:RetryCount"));
    }

    [Fact]
    public void IdleEvictionMs_MustBePositive()
    {
        var result = Validate(o => o.IdleEvictionMs = 0);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("RawChannels:IdleEvictionMs"));
    }

    [Fact]
    public void MalformedSeedChannelKey_FailsAtStartup()
    {
        var result = Validate(o => o.Seed["not-a-netid"] = new() { ["0x11:1"] = "00" });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("not-a-netid"));
    }

    [Fact]
    public void MalformedSeedSlotKey_FailsAtStartup()
    {
        var result = Validate(o =>
            o.Seed["1.2.3.4.5.6:851"] = new() { ["nonsense"] = "00" });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("nonsense"));
    }

    [Fact]
    public void MalformedSeedPayload_FailsAtStartup()
    {
        var result = Validate(o =>
            o.Seed["1.2.3.4.5.6:851"] = new() { ["0x11:1"] = "ABC" });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("ABC"));
    }

    /// <summary>
    /// An octet outside 0-255 must fail the host, not be laundered.
    /// </summary>
    /// <remarks>
    /// <c>AmsNetId.TryParse("999.1.1.1.1.1")</c> returns <see langword="true"/> and
    /// yields <c>0.1.1.1.1.1</c>, so a validator delegating to it would accept this
    /// key and seed a channel the operator never named — silent corruption rather
    /// than a startup failure.
    /// </remarks>
    [Theory]
    [InlineData("999.1.1.1.1.1:851")]
    [InlineData("1.2.3.4.5.256:851")]
    public void SeedChannelKeyWithOutOfRangeOctet_FailsAtStartup(string key)
    {
        var result = Validate(o => o.Seed[key] = new() { ["0x11:1"] = "00" });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(key));
    }

    [Fact]
    public void WellFormedSeed_Passes()
    {
        var result = Validate(o =>
            o.Seed["192.168.1.10.3.1:0xFFFF"] = new() { ["0x11:1001"] = "02000000410C0000" });
        Assert.False(result.Failed);
    }
}
