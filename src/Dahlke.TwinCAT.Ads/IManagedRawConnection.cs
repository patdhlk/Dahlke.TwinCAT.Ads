using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Internal seam capturing the operations <see cref="AdsRawChannel"/> needs from
/// an underlying raw ADS transport.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the role <see cref="IManagedConnection"/> plays for the symbol layer:
/// it exists so the facade's timeout, retry, eviction and subscription-durability
/// logic can be tested without hardware, and so the Beckhoff obsolete-overload
/// suppression is confined to a single implementation file.
/// </para>
/// <para>
/// <b>Notification callbacks use <see cref="ReadOnlyMemory{T}"/> here, not
/// <see cref="ReadOnlySpan{T}"/>.</b> The span-based
/// <see cref="RawNotificationHandler"/> is the PUBLIC contract; converting at the
/// public boundary keeps this seam expressible as a plain
/// <see cref="Action{T}"/> while still denying consumers the ability to retain a
/// buffer past their callback.
/// </para>
/// </remarks>
internal interface IManagedRawConnection : IDisposable
{
    /// <summary>Opens the transport. Throws if it cannot be opened.</summary>
    void Connect();

    /// <summary>Whether the transport currently reports itself connected.</summary>
    bool IsConnected { get; }

    /// <summary>Reads into <paramref name="destination"/>; returns bytes actually read.</summary>
    Task<int> ReadAsync(uint indexGroup, uint indexOffset, Memory<byte> destination, CancellationToken ct);

    /// <summary>Writes <paramref name="source"/>.</summary>
    Task WriteAsync(uint indexGroup, uint indexOffset, ReadOnlyMemory<byte> source, CancellationToken ct);

    /// <summary>Writes then reads in one round trip; returns bytes actually read.</summary>
    Task<int> ReadWriteAsync(
        uint indexGroup, uint indexOffset,
        Memory<byte> destination, ReadOnlyMemory<byte> source, CancellationToken ct);

    /// <summary>Reads the device's ADS and device state.</summary>
    Task<StateInfo> ReadStateAsync(CancellationToken ct);

    /// <summary>
    /// Registers a device notification. Returns the transport-level handle used
    /// to remove it. <paramref name="onData"/> is invoked on the transport's
    /// notification thread.
    /// </summary>
    Task<uint> AddNotificationAsync(
        uint indexGroup, uint indexOffset, int length, int cycleTimeMs,
        Action<ReadOnlyMemory<byte>> onData, CancellationToken ct);

    /// <summary>Removes a previously registered notification.</summary>
    Task RemoveNotificationAsync(uint handle, CancellationToken ct);
}

/// <summary>
/// Creates an unopened <see cref="IManagedRawConnection"/> for one AMS target.
/// </summary>
/// <remarks>
/// The factory is a delegate rather than an interface so tests can substitute a
/// counting or fault-injecting creator inline. The channel calls this every time
/// it needs a FRESH transport — on first use, after idle eviction, and on each
/// retry (a retry that reused the stalled transport would not reproduce the
/// behaviour consumers rely on today).
/// </remarks>
internal delegate IManagedRawConnection ManagedRawConnectionFactory(string amsNetId, int port);
