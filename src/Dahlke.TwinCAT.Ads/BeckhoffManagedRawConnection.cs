using System.Collections.Concurrent;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// The real <see cref="IManagedRawConnection"/>, wrapping a Beckhoff
/// <see cref="AdsClient"/> bound to one <c>(amsNetId, port)</c> pair.
/// </summary>
/// <remarks>
/// <b>This file is the single permitted site for <c>CS0618</c> suppression.</b>
/// The index-group/offset overloads are marked obsolete in Beckhoff 7.x, but the
/// suggested replacements require an <c>AdsConnection</c> rather than an
/// <see cref="AdsClient"/>. Confining the suppression here keeps it out of
/// consumer code, which is where it currently lives.
/// </remarks>
internal sealed class BeckhoffManagedRawConnection : IManagedRawConnection
{
    private readonly AdsClient _client = new();
    private readonly string _amsNetId;
    private readonly int _port;
    private readonly ConcurrentDictionary<uint, Action<ReadOnlyMemory<byte>>> _handlers = new();

    public BeckhoffManagedRawConnection(string amsNetId, int port)
    {
        _amsNetId = amsNetId;
        _port = port;
        _client.AdsNotification += OnAdsNotification;
    }

    public bool IsConnected => _client.IsConnected;

    public void Connect() => _client.Connect(AmsNetId.Parse(_amsNetId), _port);

    public async Task<int> ReadAsync(uint ig, uint io, Memory<byte> destination, CancellationToken ct)
    {
#pragma warning disable CS0618 // index-group overloads: see type remarks
        var result = await _client.ReadAsync(ig, io, destination, ct).ConfigureAwait(false);
#pragma warning restore CS0618
        ThrowIfFailed(result.Failed, result.ErrorCode, "Read", $"at index group 0x{ig:X} offset {io}");
        return result.ReadBytes;
    }

    public async Task WriteAsync(uint ig, uint io, ReadOnlyMemory<byte> source, CancellationToken ct)
    {
#pragma warning disable CS0618
        var result = await _client.WriteAsync(ig, io, source, ct).ConfigureAwait(false);
#pragma warning restore CS0618
        ThrowIfFailed(result.Failed, result.ErrorCode, "Write", $"at index group 0x{ig:X} offset {io}");
    }

    public async Task<int> ReadWriteAsync(
        uint ig, uint io, Memory<byte> destination, ReadOnlyMemory<byte> source, CancellationToken ct)
    {
#pragma warning disable CS0618
        var result = await _client.ReadWriteAsync(ig, io, destination, source, ct).ConfigureAwait(false);
#pragma warning restore CS0618
        ThrowIfFailed(result.Failed, result.ErrorCode, "ReadWrite", $"at index group 0x{ig:X} offset {io}");
        return result.ReadBytes;
    }

    public async Task<StateInfo> ReadStateAsync(CancellationToken ct)
    {
        var result = await _client.ReadStateAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(result.Failed, result.ErrorCode, "ReadState");
        return result.State;
    }

    public async Task<uint> AddNotificationAsync(
        uint ig, uint io, int length, int cycleTimeMs,
        Action<ReadOnlyMemory<byte>> onData, CancellationToken ct)
    {
        var settings = new NotificationSettings(
            AdsTransMode.OnChange, cycleTimeMs, maxDelay: 0);

        var result = await _client
            .AddDeviceNotificationAsync(ig, io, length, settings, userData: null!, ct)
            .ConfigureAwait(false);
        ThrowIfFailed(result.Failed, result.ErrorCode, "AddDeviceNotification", $"at index group 0x{ig:X} offset {io}");

        _handlers[result.Handle] = onData;
        return result.Handle;
    }

    public async Task RemoveNotificationAsync(uint handle, CancellationToken ct)
    {
        _handlers.TryRemove(handle, out _);
        var result = await _client.DeleteDeviceNotificationAsync(handle, ct).ConfigureAwait(false);
        ThrowIfFailed(result.Failed, result.ErrorCode, "DeleteDeviceNotification", $"for handle {handle}");
    }

    /// <summary>
    /// Beckhoff result types carry <c>Failed</c>/<c>ErrorCode</c> and have no
    /// <c>ThrowOnError()</c>; this mirrors the throw shape used throughout
    /// <see cref="AdsConnection"/> so raw failures read like every other ADS failure.
    /// </summary>
    /// <param name="failed">Whether the operation's result reported failure.</param>
    /// <param name="errorCode">The ADS error code to report and to embed in the thrown exception.</param>
    /// <param name="operation">The operation name, e.g. <c>"Read"</c> or <c>"DeleteDeviceNotification"</c>.</param>
    /// <param name="location">
    /// Optional operation-specific detail (index group/offset, handle, ...) appended
    /// to the message. Omitted entirely for operations, such as
    /// <see cref="ReadStateAsync"/>, that have no such target.
    /// </param>
    private void ThrowIfFailed(bool failed, AdsErrorCode errorCode, string operation, string? location = null)
    {
        if (!failed)
            return;

        var where = location is null ? string.Empty : $" {location}";
        throw new AdsErrorException(
            $"{operation} on raw channel {_amsNetId}:{_port}{where} failed: {errorCode}",
            errorCode);
    }

    /// <summary>
    /// Fans a transport notification out to the registered handler. The event
    /// args' buffer is valid only for this call, which is exactly the lifetime
    /// the public span-based handler enforces.
    /// </summary>
    private void OnAdsNotification(object? sender, AdsNotificationEventArgs e)
    {
        if (_handlers.TryGetValue(e.Handle, out var handler))
            handler(e.Data);
    }

    public void Dispose()
    {
        _client.AdsNotification -= OnAdsNotification;
        _handlers.Clear();
        _client.Dispose();
    }
}
