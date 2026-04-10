namespace Dahlke.TwinCAT.Ads;

public sealed class PlcTargetOptions
{
    public string AmsNetId { get; set; } = string.Empty;
    public int Port { get; set; } = 851;
    public string DisplayName { get; set; } = string.Empty;
    public int TimeoutMs { get; set; } = 5000;
}
