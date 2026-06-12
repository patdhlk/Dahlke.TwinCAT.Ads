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

    /// <summary>Number of times <see cref="LogSymbolTree"/> was called.</summary>
    public int LogSymbolTreeCount;

    private SymbolDumpOptions? _lastLogSymbolTreeOptions;

    /// <summary>
    /// The <see cref="SymbolDumpOptions"/> passed to the most recent
    /// <see cref="LogSymbolTree"/> call, or <see langword="null"/> if it
    /// has not been called yet. Written on the pool's loop thread,
    /// read on the test thread — hence the volatile access.
    /// </summary>
    public SymbolDumpOptions? LastLogSymbolTreeOptions => Volatile.Read(ref _lastLogSymbolTreeOptions);

    // Settable so unit tests that drive the facade directly (via SetCurrent,
    // bypassing Connect) can present a connected connection. Connect/Disconnect
    // also write it for the pool-loop tests.
    public bool IsConnected { get; set; }

    // Not exercised by the pool; no-op implementations to satisfy IAdsConnection.
    public ConnectionState State => ConnectionState.Disconnected;
#pragma warning disable CS0067
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
#pragma warning restore CS0067

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

    public void LogSymbolTree(SymbolDumpOptions options)
    {
        Interlocked.Increment(ref LogSymbolTreeCount);
        Volatile.Write(ref _lastLogSymbolTreeOptions, options);
    }

    public void Dispose()
    {
        Interlocked.Increment(ref DisposeCount);
        IsConnected = false;
    }

    // ---- IAdsConnection members never exercised by the pool --------------

    public Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct)
        => throw new NotSupportedException();

    public Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct)
        => throw new NotSupportedException();

    public Task WriteValueAsync(string symbolPath, object value, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<Dictionary<string, object?>> ReadValuesAsync(IEnumerable<string> symbolPaths, CancellationToken ct)
        => throw new NotSupportedException();

    public Task WriteValuesAsync(Dictionary<string, object> values, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<AdsState> GetAdsStateAsync(CancellationToken ct)
        => throw new NotSupportedException();

    // ---- Subscription support (durable-subscription tests) ---------------

    /// <summary>
    /// One recorded call to <see cref="SubscribeAsync"/>: the arguments the
    /// facade re-registered with, plus the recording disposable handed back so a
    /// test can assert whether the underlying registration was disposed.
    /// </summary>
    public sealed class SubscriptionRecord(string path, int cycleTimeMs, Action<string, object?> callback)
    {
        public string Path { get; } = path;
        public int CycleTimeMs { get; } = cycleTimeMs;
        public Action<string, object?> Callback { get; } = callback;

        private int _disposed;

        /// <summary>True once the underlying registration disposable was disposed.</summary>
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>
        /// The disposable returned to the facade. Idempotent; flips
        /// <see cref="IsDisposed"/> on first call.
        /// </summary>
        public IDisposable Disposable => new RecordingDisposable(this);

        internal void MarkDisposed() => Interlocked.Exchange(ref _disposed, 1);

        /// <summary>Simulate a PLC notification firing for this subscription.</summary>
        public void FireNotification(object? value) => Callback(Path, value);

        private sealed class RecordingDisposable(SubscriptionRecord owner) : IDisposable
        {
            private Action? _dispose = owner.MarkDisposed;
            public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }

    private readonly ConcurrentQueue<SubscriptionRecord> _subscriptions = new();

    /// <summary>All subscriptions registered against this connection, in order.</summary>
    public IReadOnlyCollection<SubscriptionRecord> Subscriptions => _subscriptions;

    /// <summary>
    /// When set, the NEXT <see cref="SubscribeAsync"/> call throws this exception
    /// once and then clears itself — used to script a one-shot re-registration
    /// failure that the facade must log and retry on the next reconnect.
    /// </summary>
    public Exception? SubscribeThrowsOnce { get; set; }

    /// <summary>
    /// When set, <see cref="SubscribeAsync"/> awaits this task before recording
    /// and returning — lets a test hold a re-registration in flight while it
    /// disposes the handle, to exercise the dispose-vs-inflight race.
    /// </summary>
    public Task? SubscribeGate { get; set; }

    private TaskCompletionSource _subscribeCalled = NewTcsT();

    private static TaskCompletionSource NewTcsT()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when <see cref="SubscribeAsync"/> is entered. Re-arm with <see cref="RearmSubscribeCalled"/>.</summary>
    public Task SubscribeCalled
    {
        get { lock (_gate) { return _subscribeCalled.Task; } }
    }

    public void RearmSubscribeCalled()
    {
        lock (_gate) { _subscribeCalled = NewTcsT(); }
    }

    public async Task<IDisposable> SubscribeAsync(string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct)
    {
        TaskCompletionSource toSignal;
        lock (_gate) { toSignal = _subscribeCalled; }
        toSignal.TrySetResult();

        var oneShot = SubscribeThrowsOnce;
        if (oneShot is not null)
        {
            SubscribeThrowsOnce = null;
            throw oneShot;
        }

        var gate = SubscribeGate;
        if (gate is not null)
            await gate.ConfigureAwait(false);

        var record = new SubscriptionRecord(symbolPath, cycleTimeMs, callback);
        _subscriptions.Enqueue(record);
        return record.Disposable;
    }

    /// <summary>
    /// The typed overload is never exercised against the underlying connection: the
    /// facade wraps the typed callback into the untyped shape BEFORE storing it in the
    /// durable record, so only the untyped <see cref="SubscribeAsync"/> ever reaches a
    /// managed connection. Implemented as a hard failure to assert that contract.
    /// </summary>
    public Task<IDisposable> SubscribeAsync<T>(string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct = default)
        => throw new NotSupportedException(
            "FakeManagedConnection only speaks the untyped SubscribeAsync; the facade wraps typed callbacks before reaching it.");
}
