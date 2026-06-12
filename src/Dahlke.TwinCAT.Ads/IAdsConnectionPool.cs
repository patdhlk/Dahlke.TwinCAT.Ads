using System.Diagnostics.CodeAnalysis;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Provides access to the stable per-target <see cref="IAdsConnection"/> facades
/// managed by the connection pool.
/// </summary>
/// <remarks>
/// <para>
/// The pool creates exactly one <see cref="IAdsConnection"/> facade per configured
/// target at construction time. That facade's identity never changes for the pool's
/// lifetime — it is safe to hold a reference to it indefinitely. The facade routes
/// every operation to the current live underlying connection, waiting up to the
/// target's <c>TimeoutMs</c> for a reconnection before throwing
/// <see cref="AdsConnectionUnavailableException"/> when the pool is mid-outage.
/// </para>
/// <para>
/// Because facades are created eagerly, <see cref="GetConnection"/> is total from
/// construction: it never returns <see langword="null"/> and never throws for a
/// configured identifier, regardless of whether <c>StartAsync</c> has been called
/// or whether the target has ever successfully connected.
/// </para>
/// <para>
/// Hosted-service start is never delayed by router availability. Simulated-target
/// loops connect immediately; real-target loops are deferred until the embedded
/// ADS router becomes ready (the router itself retries startup with backoff), and
/// are released automatically once it is. A facade for a real target therefore
/// reports <c>IsConnected == false</c> while the router is still coming up, then
/// transitions to connected without any caller action.
/// </para>
/// </remarks>
public interface IAdsConnectionPool
{
    /// <summary>
    /// Returns the stable <see cref="IAdsConnection"/> facade for the given PLC
    /// target identifier.
    /// </summary>
    /// <param name="plcId">
    /// The case-insensitive identifier of the configured PLC target.
    /// </param>
    /// <returns>
    /// The facade for the target. The returned instance is non-null and its identity
    /// is stable for the pool's lifetime. When the target has no live underlying
    /// connection (e.g. still connecting, mid-outage, or the pool has been stopped),
    /// the facade is still returned — operations on it will wait up to the target's
    /// <c>TimeoutMs</c> before throwing <see cref="AdsConnectionUnavailableException"/>.
    /// </returns>
    /// <exception cref="UnknownPlcTargetException">
    /// Thrown when <paramref name="plcId"/> does not match any configured target.
    /// The exception message lists all configured identifiers to aid diagnosis of
    /// typos. Use <see cref="TryGetConnection"/> for a non-throwing variant when
    /// the identifier may or may not be configured.
    /// </exception>
    IAdsConnection GetConnection(string plcId);

    /// <summary>
    /// Attempts to retrieve the stable <see cref="IAdsConnection"/> facade for the
    /// given PLC target identifier without throwing.
    /// </summary>
    /// <param name="plcId">
    /// The case-insensitive identifier of the PLC target to look up.
    /// </param>
    /// <param name="connection">
    /// When this method returns <see langword="true"/>, contains the non-null facade
    /// for the target. When this method returns <see langword="false"/>, contains
    /// <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="plcId"/> matches a configured
    /// target; <see langword="false"/> when it does not. This method never throws.
    /// </returns>
    bool TryGetConnection(string plcId, [NotNullWhen(true)] out IAdsConnection? connection);

    /// <summary>
    /// Returns all configured target identifiers mapped to their stable
    /// <see cref="IAdsConnection"/> facades.
    /// </summary>
    IReadOnlyDictionary<string, IAdsConnection> GetAllConnections();

    /// <summary>
    /// Forces a reconnection to the PLC.
    /// Terminates the current connection loop and starts a new one.
    /// </summary>
    /// <remarks>
    /// No-op for a simulated target (its in-memory state is preserved). For a real
    /// target whose loop has not yet been released — the embedded router is still
    /// coming up — this is also a no-op (logged as a warning): the loop starts on
    /// its own once the router is ready, so forcing it here would bypass the router
    /// gate.
    /// </remarks>
    void ForceReconnect(string plcId);
}
