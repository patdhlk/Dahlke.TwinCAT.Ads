namespace Dahlke.TwinCAT.Ads;

internal sealed class AdsConnectionFactory(ILoggerFactory loggerFactory) : IAdsConnectionFactory
{
    public IManagedConnection Create(string plcId, PlcTargetOptions options)
    {
        return new AdsConnection(plcId, options, loggerFactory);
    }
}
