using System.Collections.Concurrent;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Tests.Fakes;

/// <summary>
/// A store-backed <see cref="IManagedRawConnection"/> double composing the SAME
/// shared data-plane modules as <see cref="SimulatedRawConnection"/>
/// (<see cref="InMemoryPlcStore{TKey, TValue}"/> +
/// <see cref="SubscriberRegistry{TKey, TValue}"/>), so the store and fire-rule
/// semantics are one implementation pinned by their own unit tests; the contract
/// suite pins the ADAPTER glue on both raw harnesses.
/// </summary>
/// <remarks>
/// Also the fault-injection point for the facade's timeout, retry and
/// re-registration tests: set <see cref="FailNextWith"/> or
/// <see cref="StallNext"/> to make the next operation fail or hang.
/// </remarks>
internal sealed class InMemoryManagedRawConnection : IManagedRawConnection
{
    private readonly InMemoryPlcStore<(uint Ig, uint Io), byte[]> _store =
        new(changeComparer: ByteSequenceEqualityComparer.Instance);
    private readonly SubscriberRegistry<(uint Ig, uint Io), byte[]> _subscribers = new();
    private readonly ConcurrentDictionary<uint, IDisposable> _notifications = new();
    private uint _nextHandle;

    /// <summary>Number of times <see cref="Connect"/> has been called on this instance.</summary>
    public int ConnectCount { get; private set; }

    /// <summary>When set, the next operation throws this and the field resets to null.</summary>
    public Exception? FailNextWith { get; set; }

    /// <summary>When true, the next operation waits on its token forever (simulating a stall).</summary>
    public bool StallNext { get; set; }

    /// <summary>
    /// When true, EVERY operation stalls — a target that has simply stopped
    /// answering, rather than one bad call.
    /// </summary>
    /// <remarks>
    /// <see cref="StallNext"/> is one-shot and cannot express this: a call that has
    /// to burn a bound, be retried inline, and burn another needs the transport to
    /// still be unresponsive the second time.
    /// </remarks>
    public bool StallAlways { get; set; }

    /// <summary>
    /// Runs inside <see cref="AddNotificationAsync"/>, after the gate and BEFORE
    /// the handle is issued.
    /// </summary>
    /// <remarks>
    /// The injection point for racing a registration: it puts the test's code
    /// exactly where the device's answer is still outstanding, which is where
    /// disposing a handle orphans a notification if the registration commits
    /// blindly. Deterministic, where a real race would not be.
    /// </remarks>
    public Action? OnAddNotification { get; set; }

    /// <summary>
    /// Completes the first time an operation actually parks in a stall.
    /// </summary>
    /// <remarks>
    /// Lets a test advance a fake clock only once the operation is genuinely
    /// waiting on its per-attempt timeout. Without it a test would be racing the
    /// clock against the operation reaching its await point: advance too early and
    /// the bound is scheduled AFTER the clock has moved, so it never elapses.
    /// </remarks>
    public Task Stalled => _stalled.Task;

    private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Handles currently registered — asserts re-registration happened exactly once.</summary>
    public IReadOnlyCollection<uint> LiveHandles => _notifications.Keys.ToArray();

    public bool IsConnected { get; private set; }

    /// <summary>Whether <see cref="Dispose"/> has run on this instance.</summary>
    public bool Disposed => Volatile.Read(ref _disposed);

    private bool _disposed;

    /// <summary>
    /// Refuses every operation once disposed, exactly as
    /// <see cref="SimulatedRawConnection"/> and a real <c>AdsClient</c> do.
    /// </summary>
    /// <remarks>
    /// This fake's whole purpose is to mirror the raw contract INDEPENDENTLY, so
    /// it has to mirror the teeth too. Without them a test asserting "an in-flight
    /// operation is not evicted out from under" would pass whether or not the
    /// protection existed — the false-green harness this surface exists to avoid.
    /// </remarks>
    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed))
            throw new ObjectDisposedException(nameof(InMemoryManagedRawConnection));
    }

    public void Seed(uint ig, uint io, byte[] data) => _store.Seed((ig, io), data);

    public void Connect()
    {
        ConnectCount++;
        IsConnected = true;
    }

    public async Task<int> ReadAsync(uint ig, uint io, Memory<byte> destination, CancellationToken ct)
    {
        await GateAsync(ct).ConfigureAwait(false);

        if (!_store.TryRead((ig, io), out var data))
            throw new AdsErrorException("slot not seeded", AdsErrorCode.DeviceInvalidOffset);

        var count = Math.Min(data.Length, destination.Length);
        data.AsSpan(0, count).CopyTo(destination.Span);
        return count;
    }

    public async Task WriteAsync(uint ig, uint io, ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        await GateAsync(ct).ConfigureAwait(false);
        var bytes = source.ToArray();
        if (_store.Write((ig, io), bytes))
            _subscribers.Fire((ig, io), bytes);
    }

    public async Task<int> ReadWriteAsync(
        uint ig, uint io, Memory<byte> destination, ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        await WriteAsync(ig, io, source, ct).ConfigureAwait(false);
        return await ReadAsync(ig, io, destination, ct).ConfigureAwait(false);
    }

    public async Task<StateInfo> ReadStateAsync(CancellationToken ct)
    {
        await GateAsync(ct).ConfigureAwait(false);
        return new StateInfo(AdsState.Run, (ushort)0);
    }

    public async Task<uint> AddNotificationAsync(
        uint ig, uint io, int length, int cycleTimeMs,
        Action<ReadOnlyMemory<byte>> onData, CancellationToken ct)
    {
        await GateAsync(ct).ConfigureAwait(false);
        OnAddNotification?.Invoke();
        var handle = Interlocked.Increment(ref _nextHandle);
        _notifications[handle] = _subscribers.Subscribe((ig, io), (_, data) => onData(data));
        return handle;
    }

    public async Task RemoveNotificationAsync(uint handle, CancellationToken ct)
    {
        await GateAsync(ct).ConfigureAwait(false);
        if (_notifications.TryRemove(handle, out var registration))
            registration.Dispose();
    }

    private async Task GateAsync(CancellationToken ct)
    {
        // Every operation funnels through here, so one check covers all six.
        ThrowIfDisposed();

        if (FailNextWith is { } failure)
        {
            FailNextWith = null;
            throw failure;
        }

        if (StallNext || StallAlways)
        {
            StallNext = false;
            _stalled.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
        IsConnected = false;
        foreach (var handle in _notifications.Keys)
            if (_notifications.TryRemove(handle, out var registration))
                registration.Dispose();
    }
}
