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
/// <b>A fresh instance per <see cref="ManagedRawConnectionFactory"/> call.</b> The
/// durable slots live in the <see cref="SimulatedRawStore"/> this wraps, which the
/// factory owns, so seeded fixtures and runtime writes outlive an idle eviction
/// while <see cref="AdsRawChannel"/>'s retry (which re-creates the transport) and
/// its <c>ReferenceEquals</c> drop guard both keep working.
/// </para>
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
/// ignored and no coalescing is performed. They are per-connection, not per-store:
/// disposing this transport drops them, exactly as disposing a real
/// <c>AdsClient</c> drops its notification registrations.
/// </para>
/// <para>
/// <b>Disposal is enforced, not advisory.</b> Every operation throws
/// <see cref="ObjectDisposedException"/> once <see cref="Dispose"/> has run, the
/// way a disposed <c>AdsClient</c> would. Without that, a transport evicted out
/// from under an in-flight call would keep serving reads out of the shared store
/// and a test asserting the call is protected could not fail.
/// </para>
/// </remarks>
internal sealed class SimulatedRawConnection : IManagedRawConnection
{
    private readonly SimulatedRawStore _store;
    private readonly ConcurrentDictionary<uint, Subscription> _subscriptions = new();
    private readonly string _amsNetId;
    private readonly int _port;
    private readonly object _handleLock = new();
    private uint _nextHandle = 1;
    private bool _disposed;

    public SimulatedRawConnection(string amsNetId, int port, SimulatedRawStore store)
    {
        _amsNetId = amsNetId;
        _port = port;
        _store = store;
    }

    private sealed record Subscription(uint Ig, uint Io, Action<ReadOnlyMemory<byte>> OnData);

    public bool IsConnected { get; private set; }

    public void Connect() => IsConnected = true;

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(
                nameof(SimulatedRawConnection),
                $"Raw channel {_amsNetId}:{_port} used after its transport was disposed.");
    }

    public Task<int> ReadAsync(uint ig, uint io, Memory<byte> destination, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (!_store.Slots.TryGetValue((ig, io), out var data))
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
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        _store.Slots[(ig, io)] = source.ToArray();
        FireNotifications(ig, io);
        return Task.CompletedTask;
    }

    public async Task<int> ReadWriteAsync(
        uint ig, uint io, Memory<byte> destination, ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        ThrowIfDisposed();
        await WriteAsync(ig, io, source, ct).ConfigureAwait(false);
        return await ReadAsync(ig, io, destination, ct).ConfigureAwait(false);
    }

    public Task<StateInfo> ReadStateAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new StateInfo(AdsState.Run, (ushort)0));
    }

    public Task<uint> AddNotificationAsync(
        uint ig, uint io, int length, int cycleTimeMs,
        Action<ReadOnlyMemory<byte>> onData, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        uint handle;
        lock (_handleLock)
            handle = _nextHandle++;

        _subscriptions[handle] = new Subscription(ig, io, onData);
        return Task.FromResult(handle);
    }

    public Task RemoveNotificationAsync(uint handle, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        _subscriptions.TryRemove(handle, out _);
        return Task.CompletedTask;
    }

    private void FireNotifications(uint ig, uint io)
    {
        if (!_store.Slots.TryGetValue((ig, io), out var data))
            return;

        foreach (var (_, subscription) in _subscriptions)
            if (subscription.Ig == ig && subscription.Io == io)
                subscription.OnData(data);
    }

    public void Dispose()
    {
        _disposed = true;
        IsConnected = false;
        _subscriptions.Clear();
    }
}
