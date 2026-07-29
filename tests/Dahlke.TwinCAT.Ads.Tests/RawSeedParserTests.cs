namespace Dahlke.TwinCAT.Ads.Tests;

public class RawSeedParserTests
{
    // ------------------------------------------------------------------
    // IsWellFormedNetId
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("192.168.1.10.3.1")]
    [InlineData("5.1.2.3.4.5")]
    [InlineData("0.0.0.0.0.0")]                     // 0 and 255 are the
    [InlineData("255.255.255.255.255.255")]         // boundaries
    [InlineData("01.2.3.4.5.6")]                    // non-canonical but in range
    public void IsWellFormedNetId_AcceptsSixInRangeOctets(string netId) =>
        Assert.True(RawSeedParser.IsWellFormedNetId(netId));

    /// <summary>
    /// The octet range is checked HERE rather than delegated to
    /// <c>AmsNetId.TryParse</c>, which LAUNDERS an out-of-range octet —
    /// <c>"999.1.1.1.1.1"</c> parses true and silently becomes
    /// <c>"0.1.1.1.1.1"</c>, so <c>256</c>, <c>300</c> and <c>999</c> all collapse
    /// to one address. Delegating would let a seed entry naming a device that does
    /// not exist pass startup validation and then seed a channel the operator never
    /// named. Counting six segments has the same hole.
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
    public void IsWellFormedNetId_RejectsMalformed(string netId) =>
        Assert.False(RawSeedParser.IsWellFormedNetId(netId));

    // ------------------------------------------------------------------
    // TryParseIndex
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("0x11", 0x11u)]
    [InlineData("17", 17u)]
    [InlineData("0XF302", 0xF302u)]                 // the prefix is case-insensitive
    [InlineData("0x10180002", 0x10180002u)]
    [InlineData("0", 0u)]
    [InlineData("4294967295", uint.MaxValue)]
    [InlineData("0xFFFFFFFF", uint.MaxValue)]
    public void TryParseIndex_AcceptsDecimalAndHex(string text, uint expected)
    {
        Assert.True(RawSeedParser.TryParseIndex(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0xZZ")]
    [InlineData("nonsense")]
    [InlineData("-1")]                              // an ADS index is unsigned
    [InlineData("+1")]                              // no sign
    [InlineData(" 1")]                              // no whitespace
    [InlineData("1 ")]
    [InlineData(" 0x1")]
    [InlineData("4294967296")]                      // one past uint.MaxValue
    [InlineData("0x100000000")]
    public void TryParseIndex_RejectsMalformed(string text) =>
        Assert.False(RawSeedParser.TryParseIndex(text, out _));

    // ------------------------------------------------------------------
    // TryParseHex
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("02000000", new byte[] { 0x02, 0x00, 0x00, 0x00 })]
    [InlineData("0x02FF", new byte[] { 0x02, 0xFF })]
    [InlineData("", new byte[0])]
    public void TryParseHex_AcceptsEvenLengthPayloads(string value, byte[] expected)
    {
        Assert.True(RawSeedParser.TryParseHex(value, out var bytes, out var err));
        Assert.Equal(expected, bytes);
        Assert.Null(err);
    }

    [Theory]
    [InlineData("ABC")]      // odd length
    [InlineData("ZZ")]       // not hex
    public void TryParseHex_RejectsMalformed(string value)
    {
        Assert.False(RawSeedParser.TryParseHex(value, out _, out var err));
        Assert.NotNull(err);
        Assert.Contains(value, err);
    }
}
