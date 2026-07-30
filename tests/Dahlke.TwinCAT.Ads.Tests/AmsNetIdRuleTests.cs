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
}
