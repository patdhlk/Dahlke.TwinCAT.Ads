using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Unit-tests <see cref="AdsVersionFormatter.Format"/> — the dotted <c>major.minor.build</c>
/// formatting used by <see cref="AdsConnection.GetDeviceInfoAsync"/> — directly against a
/// hardware-free <see cref="AdsVersion"/>, since <see cref="AdsConnection"/> itself has no seam
/// for its concrete <c>AdsClient</c> field (see <see cref="AdsVersionFormatter"/>'s remarks).
/// </summary>
public class AdsVersionFormatterTests
{
    [Fact]
    public void Format_OrdinaryValues_ProducesMajorDotMinorDotBuild_InOrder()
    {
        var version = new AdsVersion(3, 1, 4024);

        var result = AdsVersionFormatter.Format(version);

        Assert.Equal("3.1.4024", result);
    }

    [Fact]
    public void Format_ZeroValues_ProducesZeroDotZeroDotZero()
    {
        var version = new AdsVersion(0, 0, 0);

        var result = AdsVersionFormatter.Format(version);

        Assert.Equal("0.0.0", result);
    }
}
