using System.Collections.Concurrent;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Tests.Fakes;

/// <summary>
/// A store-backed <see cref="IManagedRawConnection"/> double whose data plane
/// mirrors the documented raw contract INDEPENDENTLY of
/// <see cref="SimulatedRawConnection"/>, so the contract suite pins both.
/// </summary>
/// <remarks>
/// Also the fault-injection point for the facade's timeout, retry and
/// re-registration tests: set <see cref="FailNextWith"/> or
/// <see cref="StallNext"/> to make the next operation fail or hang.
/// </remarks>
internal sealed class InMemoryManagedRawConnection : IManagedRawConnection
{
    private readonly ConcurrentDictionary<(uint Ig, uint Io), byte[]> _store = new();
    private readonly ConcurrentDictionary<uint, (uint Ig, uint Io, Action<ReadOnlyMemory<byte>> OnData)> _subs = new();
    private uint _nextHandle = 1;

    /// <summary>Number of times <see cref="Connect"/> has been called on this instance.</summary>
    public int ConnectCount { get; private set; }

    /// <summary>When set, the next operation throws this and the field resets to null.</summary>
    public Exception? FailNextWith { get; set; }

    /// <summary>When true, the next operation waits on its token forever (simulating a stall).</summary>
    public bool StallNext { get; set; }

    /// <summary>Handles currently registered — asserts re-registration happened exactly once.</summary>
    public IReadOnlyCollection<uint> LiveHandles => _subs.Keys.ToArray();

    public bool IsConnected { get; private set; }
    public bool Disposed { get; private set; }

    public void Seed(uint ig, uint io, byte[] data) => _store[(ig, io)] = data;

    public void Connect()
    {
        ConnectCount++;
        IsConnected = true;
    }

    public async Task<int> ReadAsync(uint ig, uint io, Memory<byte> destination, CancellationToken ct)
    {
        await GateAsync(ct).ConfigureAwait(false);

        if (!_store.TryGetValue((ig, io), out var data))
            throw new AdsErrorException("slot not seeded", AdsErrorCode.DeviceInvalidOffset);

        var count = Math.Min(data.Length, destination.Length);
        data.AsSpan(0, count).CopyTo(destination.Span);
        return count;
    }

    public async Task WriteAsync(uint ig, uint io, ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        await GateAsync(ct).ConfigureAwait(false);
        _store[(ig, io)] = source.ToArray();
        Notify(ig, io);
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
        var handle = _nextHandle++;
        _subs[handle] = (ig, io, onData);
        return handle;
    }

    public async Task RemoveNotificationAsync(uint handle, CancellationToken ct)
    {
        await GateAsync(ct).ConfigureAwait(false);
        _subs.TryRemove(handle, out _);
    }

    /// <summary>Fires every subscription watching this slot, as a real device would on change.</summary>
    private void Notify(uint ig, uint io)
    {
        foreach (var (_, sub) in _subs)
            if (sub.Ig == ig && sub.Io == io && _store.TryGetValue((ig, io), out var data))
                sub.OnData(data);
    }

    private async Task GateAsync(CancellationToken ct)
    {
        if (FailNextWith is { } failure)
        {
            FailNextWith = null;
            throw failure;
        }

        if (StallNext)
        {
            StallNext = false;
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        Disposed = true;
        IsConnected = false;
        _subs.Clear();
    }
}
