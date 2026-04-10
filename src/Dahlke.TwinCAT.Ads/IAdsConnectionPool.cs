namespace Dahlke.TwinCAT.Ads;

public interface IAdsConnectionPool
{
    IAdsConnection? GetConnection(string plcId);
    IReadOnlyDictionary<string, IAdsConnection> GetAllConnections();

    /// <summary>
    /// Erzwingt einen Neuaufbau der Verbindung zur SPS.
    /// Beendet die aktuelle Verbindungsschleife und startet eine neue.
    /// </summary>
    void ForceReconnect(string plcId);
}
