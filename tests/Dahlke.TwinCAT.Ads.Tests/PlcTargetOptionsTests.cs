namespace Dahlke.TwinCAT.Ads.Tests;

public class PlcTargetOptionsTests
{
    [Fact]
    public void SymbolBrowseTimeoutMs_defaults_to_30_seconds()
    {
        var options = new PlcTargetOptions();

        Assert.Equal(30000, options.SymbolBrowseTimeoutMs);
    }

    [Fact]
    public void SymbolBrowseTimeoutMs_is_independent_of_TimeoutMs()
    {
        var options = new PlcTargetOptions { TimeoutMs = 1000 };

        Assert.Equal(1000, options.TimeoutMs);
        Assert.Equal(30000, options.SymbolBrowseTimeoutMs);
    }
}
