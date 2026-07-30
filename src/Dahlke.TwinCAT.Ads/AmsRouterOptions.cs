namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Options for the embedded AMS TCP/IP router. <see cref="NetId"/> and
/// <see cref="Routes"/> — and ONLY those two — are populated from the
/// <c>AmsRouter</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// <b>Adding a third property here does NOT make it configurable.</b> This type is
/// bound property-by-property, not with a whole-section <c>Bind</c>: the binding step
/// assigns <c>o.Router.NetId</c> from <c>AmsRouter:NetId</c> and <c>o.Router.Routes</c>
/// from <c>AmsRouter:Routes</c>, and reads nothing else, so a new property would
/// silently stay at its default however a host spells it in <c>appsettings.json</c>.
/// That is exactly how the <c>RawChannels</c> section shipped dead, and
/// <c>OptionsSectionsAreBoundTests</c> does not catch it — verified: adding a
/// property here leaves that guard entirely green, because it covers new SECTIONS
/// on <c>TwinCatAdsOptions</c>, not new members of a section already bound this way.
/// Either extend the binding step in <c>ServiceCollectionExtensions</c> and prove it
/// with a test that binds from a real <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>,
/// or switch this type to a whole-section <c>Bind</c> as <c>RawChannels</c> uses, which
/// picks up new members for free.
/// </para>
/// <para>
/// Note this is about THIS TYPE, not about the <c>AmsRouter</c> section as a whole.
/// Beckhoff-specific keys under it — <c>AmsRouter:TcpPort</c> and the rest — do
/// reach the embedded router, because <see cref="AdsRouterService"/> hands the whole
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> to
/// <c>AmsTcpIpRouter</c> when <c>AmsRouter:NetId</c> is set. They are consumed by
/// Beckhoff's router directly and never surface as properties here.
/// </para>
/// <para>
/// <b>Remote ROUTES are the exception, which is why <see cref="Routes"/> exists.</b>
/// A <c>StaticRoutes</c> key under this section does NOT reach the router: four
/// candidate spellings were measured against the <c>AmsTcpIpRouter(IConfiguration,
/// …)</c> overload — <c>StaticRoutes:0:*</c>, <c>RemoteConnections:R:*</c>,
/// <c>Router:StaticRoutes:0:*</c> and <c>Ams:StaticRoutes:0:*</c> — and all yielded
/// ZERO routes. Beckhoff's only configuration-file source for routes is a TwinCAT
/// <c>StaticRoutes.xml</c> on disk, which does not exist on a machine without a
/// TwinCAT installation. <see cref="Routes"/> is therefore the only supported way to
/// reach a remote PLC from such a host.
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

    /// <summary>
    /// Remote routes for the embedded router to add once it has started: the
    /// devices this host needs to reach. Empty by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Binds from an <c>AmsRouter:Routes</c> JSON ARRAY:
    /// </para>
    /// <code language="json">
    /// "AmsRouter": {
    ///   "NetId": "192.168.1.220.1.1",
    ///   "Routes": [
    ///     { "Name": "rack", "NetId": "5.138.44.199.1.1", "Address": "192.168.1.223" }
    ///   ]
    /// }
    /// </code>
    /// <para>
    /// <b>This is the only supported way to reach a remote PLC on a machine without
    /// a TwinCAT installation.</b> Beckhoff's own configuration source for routes is
    /// a TwinCAT <c>StaticRoutes.xml</c> on disk, and no key under this section
    /// reaches the router's route table — see this type's remarks for the four
    /// spellings that were measured yielding zero routes. On Windows the OS router
    /// already holds the routes, which is why an unrouted embedded router looks
    /// like it works there and fails everywhere else.
    /// </para>
    /// <para>
    /// <b>Entries are added AFTER the router has started</b>, from the
    /// <c>RouterStatus.Started</c> hook and before the readiness signal releases the
    /// connection pool's real-target loops — so a pool connection never races a
    /// missing route. Adding after start is what was verified against live hardware;
    /// before-start was never isolated as a variable.
    /// </para>
    /// <para>
    /// Ignored — with a warning — when <see cref="NetId"/> is <see langword="null"/>
    /// or empty, because there is then no embedded router to add them to and the
    /// system router owns its own route table.
    /// </para>
    /// </remarks>
    public List<AmsRouteOptions> Routes { get; set; } = [];

    /// <summary>
    /// Routes the configuration binder DISCARDED, surfaced as options-validation
    /// failures by <see cref="TwinCatAdsOptionsValidator"/>.
    /// </summary>
    /// <remarks>
    /// Internal: a channel between the binding step and the validator, not part of
    /// the configuration surface — the same arrangement
    /// <c>AdsRawChannelOptions.SeedBindingErrors</c> uses, and needed for the same
    /// reason. <c>ConfigurationBinder</c> drops a collection element it cannot bind
    /// without reporting anything, so <c>"Routes": [ "rack" ]</c> would otherwise
    /// bind to an EMPTY list and leave the device unreachable with a clean startup.
    /// </remarks>
    internal List<string> RouteBindingErrors { get; } = [];
}
