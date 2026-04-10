namespace Dahlke.TwinCAT.Ads;

public interface IAdsConnectionFactory
{
    IAdsConnection Create(string plcId, PlcTargetOptions options);
}
