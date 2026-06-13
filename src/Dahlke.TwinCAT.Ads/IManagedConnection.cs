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

    /// <summary>
    /// Logs the PLC symbol tree for diagnostics.
    /// Only symbols whose depth and prefix match <paramref name="options"/> are emitted.
    /// </summary>
    void LogSymbolTree(SymbolDumpOptions options);
}
