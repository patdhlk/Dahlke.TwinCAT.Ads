using System.Collections.Concurrent;

namespace Dahlke.TwinCAT.Ads.Tests.Fakes;

/// <summary>
/// Test double for <see cref="IAdsConnectionFactory"/>. Each call to
/// <see cref="Create"/> returns the next scripted
/// <see cref="FakeManagedConnection"/> from <see cref="Queue"/>; once the
/// queue is drained, a fresh default (healthy, never-failing) connection is
/// manufactured so a loop never blocks on a missing script. Every returned
/// instance is recorded in <see cref="Created"/>.
/// </summary>
internal sealed class FakeConnectionFactory : IAdsConnectionFactory
{
    private readonly ConcurrentQueue<FakeManagedConnection> _queue = new();
    private readonly object _gate = new();

    public List<FakeManagedConnection> Created { get; } = new();

    public int CreateCount;

    /// <summary>Enqueue a scripted connection to be returned by the next Create call.</summary>
    public FakeManagedConnection Enqueue(FakeManagedConnection connection)
    {
        _queue.Enqueue(connection);
        return connection;
    }

    public IManagedConnection Create(string plcId, PlcTargetOptions options)
    {
        Interlocked.Increment(ref CreateCount);

        var connection = _queue.TryDequeue(out var scripted)
            ? scripted
            : new FakeManagedConnection(plcId, options.DisplayName);

        lock (_gate) { Created.Add(connection); }
        return connection;
    }
}
