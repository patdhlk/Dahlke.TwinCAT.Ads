using Microsoft.Extensions.Configuration;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Tests for the pure strategy-selection method
/// <see cref="AdsRouterService.UseConfigurationPassThrough"/>.
///
/// Strategy A (config pass-through) should only be chosen when an
/// <see cref="IConfiguration"/> is present AND actually contains a non-empty
/// <c>AmsRouter:NetId</c> value.  Any other combination must fall through to
/// Strategy B (typed <c>AmsTcpIpRouter(AmsNetId, ILoggerFactory)</c>).
/// </summary>
public class AdsRouterServiceStrategyTests
{
    [Fact]
    public void UseConfigurationPassThrough_NullConfiguration_ReturnsFalse()
    {
        // Strategy B must be selected when no IConfiguration is present at all
        // (pure code-first app that never registered IConfiguration in DI).
        bool result = AdsRouterService.UseConfigurationPassThrough(null);

        Assert.False(result);
    }

    [Fact]
    public void UseConfigurationPassThrough_ConfigWithoutAmsRouterNetId_ReturnsFalse()
    {
        // IConfiguration is present (as it always is in Generic Host / ASP.NET Core)
        // but does NOT contain AmsRouter:NetId.  The code-first lambda set NetId via
        // options — that value must not be silently discarded by selecting Strategy A.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SomeOtherKey"] = "value",
            })
            .Build();

        bool result = AdsRouterService.UseConfigurationPassThrough(config);

        Assert.False(result);
    }

    [Fact]
    public void UseConfigurationPassThrough_ConfigWithEmptyAmsRouterNetId_ReturnsFalse()
    {
        // An explicit empty string for AmsRouter:NetId means no effective value is
        // present; the typed-NetId path (Strategy B) should still be used.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AmsRouter:NetId"] = "",
            })
            .Build();

        bool result = AdsRouterService.UseConfigurationPassThrough(config);

        Assert.False(result);
    }

    [Fact]
    public void UseConfigurationPassThrough_ConfigWithAmsRouterNetId_ReturnsTrue()
    {
        // IConfiguration carries a proper AmsRouter:NetId value — this is the
        // classic appsettings.json / config-driven scenario.  Strategy A should be
        // selected so that TcpPort and other Beckhoff-specific keys are honoured.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AmsRouter:NetId"] = "192.168.0.1.1.1",
            })
            .Build();

        bool result = AdsRouterService.UseConfigurationPassThrough(config);

        Assert.True(result);
    }
}
