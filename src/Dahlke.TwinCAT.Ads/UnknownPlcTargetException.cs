namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Thrown when a caller requests a connection for a PLC target identifier that
/// has no corresponding entry in the configured target set.
/// </summary>
/// <remarks>
/// <para>
/// The connection pool creates one stable <see cref="IAdsConnection"/> facade per
/// CONFIGURED target at construction time. <see cref="IAdsConnectionPool.GetConnection"/>
/// therefore never returns <see langword="null"/> and never throws for a configured
/// identifier, regardless of the target's current connection state.
/// </para>
/// <para>
/// This exception is thrown only when the supplied identifier does not match any
/// configured target. The message lists every configured identifier so that a typo
/// in a target name can be diagnosed at a glance rather than requiring a debugger
/// session.
/// </para>
/// </remarks>
public sealed class UnknownPlcTargetException : Exception
{
    /// <summary>
    /// Gets the identifier that was requested but not found in the configured target set.
    /// </summary>
    public string PlcId { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="UnknownPlcTargetException"/> with
    /// the identifier that was not found and the collection of configured identifiers.
    /// The exception message includes both so the caller can diagnose a typo without
    /// attaching a debugger.
    /// </summary>
    /// <param name="plcId">The identifier that was requested but is not configured.</param>
    /// <param name="configuredIds">
    /// All identifiers present in the pool's configuration at the time of the call.
    /// When the collection is empty the message notes that no targets are configured
    /// (unusual in practice because the options validator requires at least one target,
    /// but possible when <see cref="AdsConnectionPool"/> is constructed directly in tests).
    /// </param>
    public UnknownPlcTargetException(string plcId, IEnumerable<string> configuredIds)
        : base(BuildMessage(plcId, configuredIds))
    {
        PlcId = plcId;
    }

    /// <summary>
    /// Initializes a new instance of <see cref="UnknownPlcTargetException"/> with
    /// the identifier that was not found, the collection of configured identifiers,
    /// and an inner exception that caused this failure.
    /// </summary>
    /// <param name="plcId">The identifier that was requested but is not configured.</param>
    /// <param name="configuredIds">All identifiers present in the pool's configuration.</param>
    /// <param name="innerException">The exception that caused this failure.</param>
    public UnknownPlcTargetException(string plcId, IEnumerable<string> configuredIds, Exception innerException)
        : base(BuildMessage(plcId, configuredIds), innerException)
    {
        PlcId = plcId;
    }

    /// <summary>
    /// Initializes a new instance of <see cref="UnknownPlcTargetException"/> with an
    /// explicit message. Prefer the overloads that accept <paramref name="configuredIds"/>
    /// so the diagnostic information is generated consistently.
    /// </summary>
    /// <param name="plcId">The identifier that was requested but is not configured.</param>
    /// <param name="message">A message describing the failure.</param>
    /// <param name="innerException">
    /// The exception that caused this failure, or <see langword="null"/>.
    /// </param>
    public UnknownPlcTargetException(string plcId, string message, Exception? innerException)
        : base(message, innerException)
    {
        PlcId = plcId;
    }

    private static string BuildMessage(string plcId, IEnumerable<string> configuredIds)
    {
        var ids = configuredIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        var configured = ids.Count == 0
            ? "No targets are configured."
            : $"Configured targets: {string.Join(", ", ids)}.";
        return $"Unknown PLC target '{plcId}'. {configured}";
    }
}
