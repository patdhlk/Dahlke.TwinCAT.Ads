namespace Dahlke.TwinCAT.Ads;

internal interface IAdsConnectionFactory
{
    IManagedConnection Create(string plcId, PlcTargetOptions options);
}
