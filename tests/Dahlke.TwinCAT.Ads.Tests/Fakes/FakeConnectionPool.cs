using System.Diagnostics.CodeAnalysis;

namespace Dahlke.TwinCAT.Ads.Tests.Fakes;

/// <summary>
/// Minimal <see cref="IAdsConnectionPool"/> over a fixed map of facades plus an
/// optional scripted target-status snapshot, for exercising the pool-level Rx
/// extensions and the health check through the interface. Only the members those
/// consumers use are implemented; the rest throw.
/// </summary>
internal sealed class FakeConnectionPool(
    IReadOnlyDictionary<string, IAdsConnection> connections,
    IReadOnlyList<PlcTargetStatus>? targetStates = null)
    : IAdsConnectionPool
{
    public IAdsConnection GetConnection(string plcId)
        => connections.TryGetValue(plcId, out var c)
            ? c
            : throw new UnknownPlcTargetException(plcId, connections.Keys);

    public bool TryGetConnection(string plcId, [NotNullWhen(true)] out IAdsConnection? connection)
        => connections.TryGetValue(plcId, out connection);

    public IReadOnlyDictionary<string, IAdsConnection> GetAllConnections() => connections;

    public void ForceReconnect(string plcId) => throw new NotSupportedException();

    public IReadOnlyList<PlcTargetStatus> GetTargetStates()
        => targetStates ?? throw new NotSupportedException();

    public bool TryGetSimulatedConnection(string plcId, [NotNullWhen(true)] out SimulatedAdsConnection? simulated)
        => throw new NotSupportedException();
}
