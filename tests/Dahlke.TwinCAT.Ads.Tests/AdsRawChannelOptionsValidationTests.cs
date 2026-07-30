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

    // ------------------------------------------------------------------
    // Seed
    // ------------------------------------------------------------------

    /// <summary>
    /// A seed entry with one slot, defaulted to well-formed so each test below
    /// breaks exactly one thing.
    /// </summary>
    private static AdsRawChannelSeed Seed(
        string amsNetId = "1.2.3.4.5.6",
        int port = 851,
        string indexGroup = "0x11",
        string indexOffset = "1001",
        string bytes = "02000000410C0000") =>
        new()
        {
            AmsNetId = amsNetId,
            Port = port,
            Slots = [new AdsRawChannelSeedSlot
            {
                IndexGroup = indexGroup,
                IndexOffset = indexOffset,
                Bytes = bytes,
            }],
        };

    [Fact]
    public void WellFormedSeed_Passes()
    {
        var result = Validate(o => o.Seed.Add(Seed(amsNetId: "192.168.1.10.3.1", port: 65535)));
        Assert.False(result.Failed);
    }

    [Theory]
    [InlineData("not-a-netid")]
    [InlineData("1.2.3.4.5")]     // five octets
    [InlineData("")]              // omitted in configuration
    public void MalformedSeedNetId_FailsAtStartup(string amsNetId)
    {
        var result = Validate(o => o.Seed.Add(Seed(amsNetId: amsNetId)));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains($"'{amsNetId}'"));
        Assert.Contains(result.Failures, f => f.Contains("RawChannels:Seed:0:AmsNetId"));
    }

    /// <summary>
    /// An octet outside 0-255 must fail the host, not be laundered.
    /// </summary>
    /// <remarks>
    /// <c>AmsNetId.TryParse("999.1.1.1.1.1")</c> returns <see langword="true"/> and
    /// yields <c>0.1.1.1.1.1</c>, so a validator delegating to it would accept this
    /// entry and seed a channel the operator never named — silent corruption rather
    /// than a startup failure.
    /// </remarks>
    [Theory]
    [InlineData("999.1.1.1.1.1")]
    [InlineData("1.2.3.4.5.256")]
    public void SeedNetIdWithOutOfRangeOctet_FailsAtStartup(string amsNetId)
    {
        var result = Validate(o => o.Seed.Add(Seed(amsNetId: amsNetId)));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains(amsNetId));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void SeedPortOutOfRange_FailsAtStartup(int port)
    {
        var result = Validate(o => o.Seed.Add(Seed(port: port)));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("RawChannels:Seed:0:Port"));
    }

    [Fact]
    public void MalformedSeedIndexGroup_FailsAtStartup()
    {
        var result = Validate(o => o.Seed.Add(Seed(indexGroup: "nonsense")));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("nonsense"));
        Assert.Contains(result.Failures, f => f.Contains("Slots:0:IndexGroup"));
    }

    [Fact]
    public void MalformedSeedIndexOffset_FailsAtStartup()
    {
        var result = Validate(o => o.Seed.Add(Seed(indexOffset: "-1")));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("Slots:0:IndexOffset"));
    }

    [Fact]
    public void MalformedSeedPayload_FailsAtStartup()
    {
        var result = Validate(o => o.Seed.Add(Seed(bytes: "ABC")));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("ABC"));
        Assert.Contains(result.Failures, f => f.Contains("Slots:0:Bytes"));
    }

    /// <summary>
    /// The failure names the LIST INDEX, because that is the configuration path an
    /// operator edits: two entries that are both wrong must be distinguishable.
    /// </summary>
    [Fact]
    public void EverySeedEntryIsReported_AndNamedByItsIndex()
    {
        var result = Validate(o =>
        {
            o.Seed.Add(Seed(amsNetId: "1.2.3.4.5.6"));       // valid
            o.Seed.Add(Seed(amsNetId: "bad-one"));
            o.Seed.Add(Seed(amsNetId: "bad-two"));
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("RawChannels:Seed:1:AmsNetId") && f.Contains("bad-one"));
        Assert.Contains(result.Failures, f => f.Contains("RawChannels:Seed:2:AmsNetId") && f.Contains("bad-two"));
    }

    /// <summary>
    /// A seed entry with no slots is legitimate: it declares a reachable but empty
    /// target. Only a MALFORMED slot is a failure.
    /// </summary>
    [Fact]
    public void SeedEntryWithNoSlots_Passes()
    {
        var result = Validate(o =>
            o.Seed.Add(new AdsRawChannelSeed { AmsNetId = "1.2.3.4.5.6", Port = 851 }));

        Assert.False(result.Failed);
    }

    /// <summary>
    /// The seed is validated in BOTH modes, so a malformed entry left behind after
    /// switching to <see cref="ConnectionMode.Real"/> still fails the host rather
    /// than sitting silently broken until someone switches back.
    /// </summary>
    [Fact]
    public void MalformedSeed_FailsEvenInRealMode()
    {
        var result = Validate(o =>
        {
            o.Mode = ConnectionMode.Real;
            o.Seed.Add(Seed(amsNetId: "not-a-netid"));
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("not-a-netid"));
    }
}
