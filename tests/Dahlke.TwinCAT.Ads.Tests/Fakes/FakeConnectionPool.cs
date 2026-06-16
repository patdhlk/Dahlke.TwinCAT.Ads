using System.Diagnostics.CodeAnalysis;

namespace Dahlke.TwinCAT.Ads.Tests.Fakes;

/// <summary>
/// Minimal <see cref="IAdsConnectionPool"/> over a fixed map of facades, for
/// exercising the pool-level Rx extensions. Only the lookup members used by those
/// extensions are implemented; the rest throw.
/// </summary>
internal sealed class FakeConnectionPool(IReadOnlyDictionary<string, IAdsConnection> connections)
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

    public bool TryGetSimulatedConnection(string plcId, [NotNullWhen(true)] out SimulatedAdsConnection? simulated)
        => throw new NotSupportedException();
}
