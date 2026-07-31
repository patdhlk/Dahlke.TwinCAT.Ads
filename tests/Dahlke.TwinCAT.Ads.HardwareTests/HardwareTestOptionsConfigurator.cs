namespace Dahlke.TwinCAT.Ads.HardwareTests;

/// <summary>
/// Wires a <see cref="PlcTargetOptions"/> — and, when configured, the embedded AMS router —
/// onto a <see cref="TwinCatAdsOptions"/> instance from <see cref="HardwareTestConfig"/>.
/// </summary>
/// <remarks>
/// Shared by <see cref="HardwareEndToEndTests"/> and <see cref="AlarmMonitorHardwareTests"/> so
/// the router wiring lives in exactly one place. Without
/// <see cref="HardwareTestConfig.HasEmbeddedRouter"/>, <c>options.Router</c> is left untouched
/// and the system router is used — unchanged Windows/system-router behaviour. See the remarks
/// on <c>AmsRouterOptions.Routes</c> for why the embedded router plus a route is the only
/// supported way to reach a remote PLC on a machine without a TwinCAT installation.
/// </remarks>
internal static class HardwareTestOptionsConfigurator
{
    private const string EmbeddedRouteName = "hardware-test-target";

    /// <summary>
    /// Registers <paramref name="target"/> under <paramref name="plcId"/> and, when
    /// <see cref="HardwareTestConfig.HasEmbeddedRouter"/> is true, points the embedded router at
    /// this host's Net ID with a single route to the target.
    /// </summary>
    public static void ConfigureTarget(TwinCatAdsOptions options, string plcId, PlcTargetOptions target)
    {
        options.Targets[plcId] = target;

        if (!HardwareTestConfig.HasEmbeddedRouter)
            return;

        options.Router.NetId = HardwareTestConfig.RouterNetId;
        options.Router.Routes.Add(new AmsRouteOptions
        {
            Name = EmbeddedRouteName,
            NetId = HardwareTestConfig.AmsNetId,
            Address = HardwareTestConfig.RouteAddress!,
        });
    }
}
