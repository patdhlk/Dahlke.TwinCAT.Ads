namespace Dahlke.TwinCAT.Ads;

public interface IAdsConnectionPool
{
    IAdsConnection? GetConnection(string plcId);
    IReadOnlyDictionary<string, IAdsConnection> GetAllConnections();

    /// <summary>
    /// Forces a reconnection to the PLC.
    /// Terminates the current connection loop and starts a new one.
    /// </summary>
    void ForceReconnect(string plcId);
}
