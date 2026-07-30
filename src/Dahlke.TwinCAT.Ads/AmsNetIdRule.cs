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

    /// <summary>
    /// Appends a startup failure when <paramref name="value"/> is not strictly
    /// well-formed. The single message template for every configured Net ID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes the configuration PATH rather than a friendly label, because the path is
    /// the only identifier that is both searchable and actionable — an index alone is
    /// unsearchable and a value alone is ambiguous across entries. Leading with it is
    /// why the message needs no trailing "Fix '…'" clause.
    /// </para>
    /// <para>
    /// Appends to the caller's list rather than returning a <see cref="bool"/>, so the
    /// validator's own idiom — collect every failure, report them together, let the
    /// operator fix one boot instead of five — survives at each call site as a single
    /// line.
    /// </para>
    /// </remarks>
    /// <param name="configPath">
    /// The configuration path of the offending key, e.g.
    /// <c>PlcTargets:myPlc:AmsNetId</c> or <c>AmsRouter:Routes:0:NetId</c>.
    /// </param>
    /// <param name="value">The offending value, quoted verbatim into the message.</param>
    /// <param name="failures">The validator's collected failures, appended to.</param>
    /// <param name="remedy">
    /// Optional key-specific recovery advice, appended to the SAME message so a site
    /// with its own escape hatch does not cost the operator a second failure. Only
    /// <c>AmsRouter:NetId</c> passes it — removing that key falls back to the system
    /// router, which no other Net ID key offers.
    /// </param>
    internal static void Require(
        string configPath,
        string? value,
        List<string> failures,
        string? remedy = null)
    {
        if (IsWellFormed(value))
            return;

        var message =
            $"{configPath} '{value}' is not six dot-separated octets in the " +
            $"range 0-255 (e.g. '192.168.1.10.1.1').";

        failures.Add(remedy is null ? message : $"{message} {remedy}");
    }
}
