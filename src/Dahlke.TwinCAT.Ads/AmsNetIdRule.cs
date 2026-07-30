using System.Globalization;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// The single definition of what counts as a well-formed AMS Net ID, and the two
/// proportionate responses to one that is not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately NOT delegated to <c>TwinCAT.Ads.AmsNetId.TryParse</c>.</b> That
/// method LAUNDERS an out-of-range octet instead of rejecting it:
/// <c>AmsNetId.TryParse("999.1.1.1.1.1")</c> returns <see langword="true"/> and
/// yields <c>0.1.1.1.1.1</c> — the octet is ZEROED, not reduced modulo 256, so
/// <c>256</c>, <c>257</c>, <c>300</c>, <c>512</c> and <c>999</c> all collapse to the
/// same address. Counting six segments has the same hole.
/// </para>
/// <para>
/// <b>Measured, not folklore:</b>
/// <c>AmsRouterRouteTests.AmsNetIdTryParse_LaundersAnOutOfRangeOctet</c> pins that
/// behaviour as an executable fact, because the whole argument for this module rests
/// on it. If a future Beckhoff version starts rejecting the value instead, that test
/// says so and this reasoning can be revisited deliberately.
/// </para>
/// <para>
/// <b>One rule, two proportionate responses.</b> A configured Net ID is a
/// DECLARATION, so <see cref="Require"/> REJECTS a laundering value — the host fails
/// at startup rather than booting and addressing a device nobody wrote down. A
/// caller-supplied Net ID is a runtime LOOKUP, so <c>Normalise</c> ACCEPTS one:
/// <c>IAdsRawChannelFactory.Get</c> is documented total, and the transport launders
/// identically at <c>Connect()</c>, so collapsing the spellings keeps the channel key
/// agreeing with the wire. It reports the laundering instead of hiding it, and the
/// caller warns. Both rest on <see cref="IsWellFormed"/>, so the two responses can
/// never disagree about the rule they are responding to.
/// </para>
/// <para>
/// Exposed as <see langword="internal"/> so <c>Dahlke.TwinCAT.Ads.Tests</c> (via
/// <c>InternalsVisibleTo</c>) can cover the grammar directly.
/// </para>
/// </remarks>
internal static class AmsNetIdRule
{
    /// <summary>
    /// Six dot-separated decimal octets, each in 0-255.
    /// </summary>
    /// <remarks>
    /// <see cref="NumberStyles.None"/> per octet: no sign, no whitespace, no hex, so
    /// <c>"+1"</c>, <c>" 1"</c> and <c>"0x1"</c> are all malformed in an AMS Net ID.
    /// A <see langword="null"/> is malformed rather than an exception, because the
    /// route and seed sites pass a bound property unguarded and a key that never
    /// bound must surface as a startup failure naming it.
    /// </remarks>
    internal static bool IsWellFormed(string? text)
    {
        if (text is null)
            return false;

        var octets = text.Split('.');
        if (octets.Length != 6)
            return false;

        foreach (var octet in octets)
        {
            if (!byte.TryParse(octet, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                return false;
        }

        return true;
    }
}
