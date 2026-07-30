namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Unit tests for <see cref="AmsNetIdRule"/> — the single definition of a
/// well-formed AMS Net ID, and the two proportionate responses to one that is not.
/// </summary>
public class AmsNetIdRuleTests
{
    // ------------------------------------------------------------------
    // IsWellFormed
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("192.168.1.10.3.1")]
    [InlineData("5.1.2.3.4.5")]
    [InlineData("0.0.0.0.0.0")]                     // 0 and 255 are the
    [InlineData("255.255.255.255.255.255")]         // boundaries
    [InlineData("01.2.3.4.5.6")]                    // non-canonical but in range
    public void IsWellFormed_AcceptsSixInRangeOctets(string netId) =>
        Assert.True(AmsNetIdRule.IsWellFormed(netId));

    /// <summary>
    /// The octet range is checked HERE rather than delegated to
    /// <c>AmsNetId.TryParse</c>, which LAUNDERS an out-of-range octet —
    /// <c>"999.1.1.1.1.1"</c> parses true and silently becomes <c>"0.1.1.1.1.1"</c>,
    /// so <c>256</c>, <c>300</c> and <c>999</c> all collapse to one address.
    /// Delegating would let a declaration naming a device that does not exist pass
    /// startup validation. Counting six segments has the same hole.
    /// </summary>
    [Theory]
    [InlineData("1.2.3.4.5")]                       // five octets
    [InlineData("1.2.3.4.5.6.7")]                   // seven
    [InlineData("999.1.1.1.1.1")]
    [InlineData("1.2.3.4.5.256")]
    [InlineData("1.2.3.4.5.-1")]                    // NumberStyles.None: no sign
    [InlineData("1.2.3.4.5.+1")]
    [InlineData("1.2.3.4.5. 1")]                    // and no whitespace
    [InlineData("1.2.3.4.5.0x10")]                  // hex is not an AMS octet
    [InlineData("abc.d.e.f.g.h")]
    [InlineData("")]
    public void IsWellFormed_RejectsMalformed(string netId) =>
        Assert.False(AmsNetIdRule.IsWellFormed(netId));

    /// <summary>
    /// A null is MALFORMED rather than an exception. The route and seed validation
    /// sites pass their bound property unguarded, so a key that never bound must
    /// produce a startup failure naming it — not a NullReferenceException from
    /// inside the validator.
    /// </summary>
    [Fact]
    public void IsWellFormed_RejectsNull_RatherThanThrowing() =>
        Assert.False(AmsNetIdRule.IsWellFormed(null));

    // ------------------------------------------------------------------
    // Require
    // ------------------------------------------------------------------

    [Fact]
    public void Require_AppendsNothing_ForAWellFormedValue()
    {
        var failures = new List<string>();

        AmsNetIdRule.Require("PlcTargets:plc1:AmsNetId", "1.2.3.4.5.6", failures);

        Assert.Empty(failures);
    }

    /// <summary>
    /// The message carries BOTH the configuration path an operator edits and the
    /// offending value. A path alone does not say which value was wrong; a value
    /// alone is unsearchable and ambiguous across entries.
    /// </summary>
    [Fact]
    public void Require_NamesTheConfigPathAndTheOffendingValue()
    {
        var failures = new List<string>();

        AmsNetIdRule.Require("AmsRouter:Routes:0:NetId", "999.1.1.1.1.1", failures);

        var failure = Assert.Single(failures);
        Assert.Contains("AmsRouter:Routes:0:NetId", failure);
        Assert.Contains("999.1.1.1.1.1", failure);
        Assert.Contains("0-255", failure);
    }

    /// <summary>
    /// ONE template across every site. Before consolidation four sites spelled this
    /// four ways and two of them omitted the octet range — the very part of the rule
    /// that distinguishes it from <c>AmsNetId.TryParse</c>, so those two messages
    /// described a rule the validator did not enforce.
    /// </summary>
    [Fact]
    public void Require_UsesOneTemplate_AcrossEverySite()
    {
        var failures = new List<string>();

        AmsNetIdRule.Require("PlcTargets:plc1:AmsNetId", "999.1.1.1.1.1", failures);
        AmsNetIdRule.Require("RawChannels:Seed:0:AmsNetId", "999.1.1.1.1.1", failures);

        Assert.Equal(2, failures.Count);
        Assert.Equal(
            failures[0].Replace("PlcTargets:plc1:AmsNetId", "<path>"),
            failures[1].Replace("RawChannels:Seed:0:AmsNetId", "<path>"));
    }

    /// <summary>
    /// A key with its own recovery path can say so without earning a SECOND failure.
    /// <c>AmsRouter:NetId</c> is the only such site: removing the key falls back to
    /// the system router, which no other Net ID key offers.
    /// </summary>
    [Fact]
    public void Require_AppendsAKeySpecificRemedy_AsOneFailure()
    {
        var failures = new List<string>();

        AmsNetIdRule.Require(
            "AmsRouter:NetId", "999.1.1.1.1.1", failures,
            remedy: "Remove the key to use the system router instead.");

        var failure = Assert.Single(failures);
        Assert.Contains("0-255", failure);
        Assert.Contains("Remove the key to use the system router instead.", failure);
    }

    /// <summary>
    /// A key that never bound fails naming itself, rather than throwing from inside
    /// the validator and costing the operator every OTHER failure in the batch.
    /// </summary>
    [Fact]
    public void Require_RejectsNull_WithoutThrowing()
    {
        var failures = new List<string>();

        AmsNetIdRule.Require("AmsRouter:Routes:0:NetId", null, failures);

        var failure = Assert.Single(failures);
        Assert.Contains("AmsRouter:Routes:0:NetId", failure);
    }
}
