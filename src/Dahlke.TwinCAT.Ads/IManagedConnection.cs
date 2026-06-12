namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Internal seam capturing the lifecycle operations the connection pool
/// needs from a connection. Layers on top of the public <see cref="IAdsConnection"/>.
/// </summary>
internal interface IManagedConnection : IAdsConnection, IDisposable
{
    void Connect();
    void Disconnect();
    Task<bool> IsAliveAsync(CancellationToken ct);
    void ForceDisconnect();
    void LogSymbolTree();
}
