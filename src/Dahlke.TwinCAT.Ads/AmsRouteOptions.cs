namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// One remote route handed to the embedded AMS router: the name, AMS Net ID and
/// network address of a device the host needs to reach.
/// </summary>
/// <remarks>
/// <para>
/// A route is the AMS-level equivalent of a host-file entry — it tells the router
/// which TCP endpoint carries traffic for an AMS Net ID. Without one the router has
/// no way to place the target and every operation against it fails with
/// <c>TargetMachineNotFound</c>, verified against a live rack: the identical code
/// path failed without a route and succeeded with one, same machine and same
/// Net ID.
/// </para>
/// <para>
/// Every member is validated at startup by <see cref="TwinCatAdsOptionsValidator"/>,
/// so a typo fails the host rather than surfacing hours later as an unreachable
/// device.
/// </para>
/// </remarks>
public sealed class AmsRouteOptions
{
    /// <summary>
    /// The route's name, as it appears in the router's route table. Required, and
    /// must be unique across <see cref="AmsRouterOptions.Routes"/>.
    /// </summary>
    /// <remarks>
    /// Beckhoff keys routes by name — <c>AmsTcpIpRouter.RemoveRoute(string)</c>
    /// takes one — so two routes sharing a name are not two routes. A duplicate is
    /// rejected at startup instead of silently collapsing.
    /// </remarks>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The remote device's AMS Net ID, as six dot-separated octets each in 0-255 —
    /// for example <c>5.138.44.199.1.1</c>. Required.
    /// </summary>
    /// <remarks>
    /// The octet range is enforced STRICTLY at startup, deliberately not via
    /// <c>TwinCAT.Ads.AmsNetId.TryParse</c>: that method ZEROES an out-of-range
    /// octet and returns <see langword="true"/>, so <c>999.1.1.1.1.1</c> would be
    /// accepted as <c>0.1.1.1.1.1</c> and the route would address a different
    /// device than the one written in configuration. See
    /// <c>RawSeedParser.IsWellFormedNetId</c>, which both this and the raw-channel
    /// seed validation share.
    /// </remarks>
    public string NetId { get; set; } = string.Empty;

    /// <summary>
    /// The remote device's network address: an IP address (<c>192.168.1.223</c>) or
    /// a host name (<c>cx-01a2b3</c>). Required.
    /// </summary>
    /// <remarks>
    /// Beckhoff's route type accepts either form and resolves a host name itself,
    /// so no distinction is drawn here. Resolution happens inside the router, which
    /// means an unresolvable host name surfaces as an unreachable target at runtime
    /// rather than as a startup failure — startup validation can only require the
    /// value to be present.
    /// </remarks>
    public string Address { get; set; } = string.Empty;
}
