namespace Dahlke.TwinCAT.Ads.Tests;

public class RawSeedParserTests
{
    [Theory]
    [InlineData("192.168.1.10.3.1:65535", "192.168.1.10.3.1", 65535)]
    [InlineData("5.1.2.3.4.5:851", "5.1.2.3.4.5", 851)]
    [InlineData("5.1.2.3.4.5:0xFFFF", "5.1.2.3.4.5", 65535)]
    [InlineData("0.0.0.0.0.0:851", "0.0.0.0.0.0", 851)]         // 0 and 255 are the
    [InlineData("255.255.255.255.255.255:1", "255.255.255.255.255.255", 1)] // boundaries
    public void TryParseChannelKey_AcceptsNetIdAndPort(string key, string netId, int port)
    {
        Assert.True(RawSeedParser.TryParseChannelKey(key, out var n, out var p, out var err));
        Assert.Equal(netId, n);
        Assert.Equal(port, p);
        Assert.Null(err);
    }

    [Theory]
    [InlineData("192.168.1.10.3.1")]        // no port
    [InlineData("1.2.3.4.5:851")]           // NetId must have six octets
    [InlineData("1.2.3.4.5.6:70000")]       // port out of range
    [InlineData("1.2.3.4.5.6:-1")]
    [InlineData("1.2.3.4.5.6:")]
    // Octets must be 0-255. AmsNetId.TryParse would LAUNDER both of these —
    // "999.1.1.1.1.1" parses true and silently becomes "0.1.1.1.1.1" — so a seed
    // key naming a device that does not exist would pass startup validation and
    // then seed a channel the operator never named. Hence the parser validates
    // octets itself rather than delegating.
    [InlineData("999.1.1.1.1.1:851")]
    [InlineData("1.2.3.4.5.256:851")]
    [InlineData("1.2.3.4.5.-1:851")]
    [InlineData("1.2.3.4.5.0x10:851")]      // hex is not an AMS octet
    [InlineData("abc.d.e.f.g.h:851")]
    public void TryParseChannelKey_RejectsMalformed(string key)
    {
        Assert.False(RawSeedParser.TryParseChannelKey(key, out _, out _, out var err));
        Assert.NotNull(err);
        Assert.Contains(key, err);
    }

    [Theory]
    [InlineData("0x11:1001", 0x11u, 1001u)]
    [InlineData("17:1001", 17u, 1001u)]
    [InlineData("0xF302:0x10180002", 0xF302u, 0x10180002u)]
    public void TryParseSlotKey_AcceptsHexAndDecimal(string key, uint ig, uint io)
    {
        Assert.True(RawSeedParser.TryParseSlotKey(key, out var g, out var o, out var err));
        Assert.Equal(ig, g);
        Assert.Equal(io, o);
        Assert.Null(err);
    }

    [Theory]
    [InlineData("0x11")]
    [InlineData("0xZZ:1")]
    [InlineData(":1001")]
    public void TryParseSlotKey_RejectsMalformed(string key)
    {
        Assert.False(RawSeedParser.TryParseSlotKey(key, out _, out _, out var err));
        Assert.NotNull(err);
    }

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
    }
}
