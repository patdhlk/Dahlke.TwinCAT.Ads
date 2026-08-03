namespace Dahlke.TwinCAT.Ads;

internal sealed class AdsConnectionFactory(ILoggerFactory loggerFactory) : IAdsConnectionFactory
{
    public IManagedConnection Create(string plcId, PlcTargetOptions options)
    {
        if (options.Mode == ConnectionMode.Simulated)
        {
            var simulated = new SimulatedAdsConnection(plcId, options.DisplayName, loggerFactory);
            simulated.SetInitialValues(options.InitialValues);
            simulated.SetSymbolAttributes(options.SymbolAttributes);
            return simulated;
        }

        return new AdsConnection(plcId, options, loggerFactory);
    }
}
