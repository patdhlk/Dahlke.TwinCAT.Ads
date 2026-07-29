using System.Collections.Concurrent;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// In-memory <see cref="IManagedRawConnection"/> backed by a seedable byte store,
/// used when <see cref="AdsRawChannelOptions.Mode"/> is
/// <see cref="ConnectionMode.Simulated"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pinned semantics.</b> An unseeded read throws
/// <see cref="AdsErrorException"/> with
/// <see cref="AdsErrorCode.DeviceInvalidOffset"/> — deliberately the code real
/// hardware answers for a bad offset, so a consumer's error-classification path
/// is exercised in simulation rather than only against a device. A seeded slot
/// shorter than the destination returns the seeded length; longer fills the
/// destination and returns its length.
/// </para>
/// <para>
/// <b><see cref="ReadWriteAsync"/> is a convention, not protocol emulation.</b>
/// It writes the source to the slot and returns the slot's bytes. Real ReadWrite
/// semantics are device-defined and this type refuses to guess them: a consumer
/// testing a file protocol seeds the response it expects — the simulation will
/// never invent a file handle.
/// </para>
/// <para>
/// <b>Subscriptions fire on write</b> to the watched slot, mirroring
/// <see cref="SimulatedAdsConnection"/>'s fire-on-change. <c>cycleTimeMs</c> is
/// ignored and no coalescing is performed.
/// </para>
/// </remarks>
internal sealed class SimulatedRawConnection : IManagedRawConnection, ISimulatedRawChannel
{
    private readonly ConcurrentDictionary<(uint Ig, uint Io), byte[]> _store = new();
    private readonly ConcurrentDictionary<uint, Subscription> _subscriptions = new();
    private readonly string _amsNetId;
    private readonly int _port;
    private readonly object _handleLock = new();
    private uint _nextHandle = 1;

    public SimulatedRawConnection(string amsNetId, int port)
    {
        _amsNetId = amsNetId;
        _port = port;
    }

    private sealed record Subscription(uint Ig, uint Io, Action<ReadOnlyMemory<byte>> OnData);

    public bool IsConnected { get; private set; }

    public void Connect() => IsConnected = true;

    public void Seed(uint indexGroup, uint indexOffset, ReadOnlySpan<byte> data) =>
        _store[(indexGroup, indexOffset)] = data.ToArray();

    public Task<int> ReadAsync(uint ig, uint io, Memory<byte> destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_store.TryGetValue((ig, io), out var data))
        {
            throw new AdsErrorException(
                $"No simulated data seeded at index group 0x{ig:X} offset {io} " +
                $"on raw channel {_amsNetId}:{_port}.",
                AdsErrorCode.DeviceInvalidOffset);
        }

        var count = Math.Min(data.Length, destination.Length);
        data.AsSpan(0, count).CopyTo(destination.Span);
        return Task.FromResult(count);
    }

    public Task WriteAsync(uint ig, uint io, ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _store[(ig, io)] = source.ToArray();
        FireNotifications(ig, io);
        return Task.CompletedTask;
    }

    public async Task<int> ReadWriteAsync(
        uint ig, uint io, Memory<byte> destination, ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        await WriteAsync(ig, io, source, ct).ConfigureAwait(false);
        return await ReadAsync(ig, io, destination, ct).ConfigureAwait(false);
    }

    public Task<StateInfo> ReadStateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new StateInfo(AdsState.Run, (ushort)0));
    }

    public Task<uint> AddNotificationAsync(
        uint ig, uint io, int length, int cycleTimeMs,
        Action<ReadOnlyMemory<byte>> onData, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        uint handle;
        lock (_handleLock)
            handle = _nextHandle++;

        _subscriptions[handle] = new Subscription(ig, io, onData);
        return Task.FromResult(handle);
    }

    public Task RemoveNotificationAsync(uint handle, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _subscriptions.TryRemove(handle, out _);
        return Task.CompletedTask;
    }

    private void FireNotifications(uint ig, uint io)
    {
        if (!_store.TryGetValue((ig, io), out var data))
            return;

        foreach (var (_, subscription) in _subscriptions)
            if (subscription.Ig == ig && subscription.Io == io)
                subscription.OnData(data);
    }

    public void Dispose()
    {
        IsConnected = false;
        _subscriptions.Clear();
    }
}
