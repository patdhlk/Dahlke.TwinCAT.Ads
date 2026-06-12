using System.Collections.Concurrent;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Tests.Fakes;

/// <summary>
/// Test double for <see cref="IManagedConnection"/> used to drive the
/// connection-pool loop deterministically.
///
/// Controllable behaviour:
///   - <see cref="ConnectShouldThrow"/> makes <see cref="Connect"/> throw.
///   - <see cref="IsAliveResults"/> scripts the sequence of health-check
///     results; once drained, <see cref="IsAliveDefault"/> is returned.
///
/// Synchronisation hooks (all re-armable, all use
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> so the
/// awaiting test never resumes inline on the loop thread):
///   - <see cref="ConnectCalled"/> completes when <see cref="Connect"/> is invoked.
///   - <see cref="IsAliveCalled"/> completes when <see cref="IsAliveAsync"/> is invoked.
///
/// A test awaits these hooks (with a real-time safety timeout) to know the
/// loop has progressed to a known point before advancing fake time.
/// </summary>
internal sealed class FakeManagedConnection : IManagedConnection
{
    private readonly object _gate = new();
    private TaskCompletionSource _connectCalled = NewTcs();
    private TaskCompletionSource _isAliveCalled = NewTcs();

    public FakeManagedConnection(string plcId = "plc1", string displayName = "PLC 1")
    {
        PlcId = plcId;
        DisplayName = displayName;
    }

    private static TaskCompletionSource NewTcs()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- Scriptable behaviour -------------------------------------------

    /// <summary>When true, <see cref="Connect"/> throws.</summary>
    public bool ConnectShouldThrow { get; set; }

    /// <summary>Exception thrown by <see cref="Connect"/> when scripted to fail.</summary>
    public Exception ConnectException { get; set; } = new InvalidOperationException("connect failed");

    /// <summary>Queued health-check results, consumed front-to-back.</summary>
    public ConcurrentQueue<bool> IsAliveResults { get; } = new();

    /// <summary>Returned by <see cref="IsAliveAsync"/> once the queue is drained.</summary>
    public bool IsAliveDefault { get; set; } = true;

    // ---- Recorded calls --------------------------------------------------

    public int ConnectCount;
    public int DisconnectCount;
    public int ForceDisconnectCount;
    public int DisposeCount;
    public int IsAliveCount;

    public bool IsConnected { get; private set; }

    // ---- Synchronisation hooks ------------------------------------------

    /// <summary>Completes when <see cref="Connect"/> is called. Re-arm with <see cref="RearmConnectCalled"/>.</summary>
    public Task ConnectCalled
    {
        get { lock (_gate) { return _connectCalled.Task; } }
    }

    /// <summary>Completes when <see cref="IsAliveAsync"/> is called. Re-arm with <see cref="RearmIsAliveCalled"/>.</summary>
    public Task IsAliveCalled
    {
        get { lock (_gate) { return _isAliveCalled.Task; } }
    }

    public void RearmConnectCalled()
    {
        lock (_gate) { _connectCalled = NewTcs(); }
    }

    public void RearmIsAliveCalled()
    {
        lock (_gate) { _isAliveCalled = NewTcs(); }
    }

    // ---- IManagedConnection ---------------------------------------------

    public string PlcId { get; }
    public string DisplayName { get; }

    public void Connect()
    {
        Interlocked.Increment(ref ConnectCount);
        TaskCompletionSource toSignal;
        lock (_gate) { toSignal = _connectCalled; }
        toSignal.TrySetResult();

        if (ConnectShouldThrow)
        {
            IsConnected = false;
            throw ConnectException;
        }
        IsConnected = true;
    }

    public void Disconnect()
    {
        Interlocked.Increment(ref DisconnectCount);
        IsConnected = false;
    }

    public void ForceDisconnect()
    {
        Interlocked.Increment(ref ForceDisconnectCount);
        IsConnected = false;
    }

    public Task<bool> IsAliveAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref IsAliveCount);
        TaskCompletionSource toSignal;
        lock (_gate) { toSignal = _isAliveCalled; }
        toSignal.TrySetResult();

        ct.ThrowIfCancellationRequested();

        var result = IsAliveResults.TryDequeue(out var scripted) ? scripted : IsAliveDefault;
        return Task.FromResult(result);
    }

    public void LogSymbolTree()
    {
    }

    public void Dispose()
    {
        Interlocked.Increment(ref DisposeCount);
        IsConnected = false;
    }

    // ---- IAdsConnection members never exercised by the pool --------------

    public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct)
        => throw new NotSupportedException();

    public Task WriteValueAsync(string symbolPath, object value, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<Dictionary<string, object?>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct)
        => throw new NotSupportedException();

    public Task WriteValuesAsync(Dictionary<string, object> values, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<AdsState> GetAdsStateAsync(CancellationToken ct)
        => throw new NotSupportedException();

    public Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct)
        => throw new NotSupportedException();
}
