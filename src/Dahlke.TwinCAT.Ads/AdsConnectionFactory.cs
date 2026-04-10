namespace Dahlke.TwinCAT.Ads;

public sealed class AdsConnectionFactory(ILoggerFactory loggerFactory) : IAdsConnectionFactory
{
    public IAdsConnection Create(string plcId, PlcTargetOptions options)
    {
        return new AdsConnection(plcId, options, loggerFactory);
    }
}
