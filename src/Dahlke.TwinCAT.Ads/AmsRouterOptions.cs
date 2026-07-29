namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Options for the embedded AMS TCP/IP router. <see cref="NetId"/> — and ONLY
/// <see cref="NetId"/> — is populated from the <c>AmsRouter</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// <b>Adding a second property here does NOT make it configurable.</b> This type is
/// bound property-by-property, not with a whole-section <c>Bind</c>: the binding step
/// assigns <c>o.Router.NetId</c> from <c>AmsRouter:NetId</c> and reads nothing else, so
/// a new property would silently stay at its default however a host spells it in
/// <c>appsettings.json</c>. That is exactly how the <c>RawChannels</c> section shipped
/// dead, and <c>OptionsSectionsAreBoundTests</c> does not catch it — verified: adding a
/// second property here leaves that guard entirely green, because it covers new SECTIONS
/// on <c>TwinCatAdsOptions</c>, not new members of a section already bound this way.
/// Either extend the binding step in <c>ServiceCollectionExtensions</c> and prove it
/// with a test that binds from a real <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>,
/// or switch this type to a whole-section <c>Bind</c> as <c>RawChannels</c> uses, which
/// picks up new members for free.
/// </para>
/// <para>
/// Note this is about THIS TYPE, not about the <c>AmsRouter</c> section as a whole.
/// Beckhoff-specific keys under it — <c>AmsRouter:TcpPort</c>, <c>StaticRoutes</c> and
/// the rest — do reach the embedded router, because <see cref="AdsRouterService"/> hands
/// the whole <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> to
/// <c>AmsTcpIpRouter</c> when <c>AmsRouter:NetId</c> is set. They are consumed by
/// Beckhoff's router directly and never surface as properties here.
/// </para>
/// </remarks>
public sealed class AmsRouterOptions
{
    /// <summary>
    /// The AMS Net ID for the embedded router to bind to.
    /// When <see langword="null"/> or empty the embedded router is disabled
    /// and the system router is used instead.
    /// </summary>
    public string? NetId { get; set; }
}
