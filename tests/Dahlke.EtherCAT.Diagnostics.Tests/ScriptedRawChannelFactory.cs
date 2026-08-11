using Dahlke.TwinCAT.Ads;
using System.Diagnostics.CodeAnalysis;
using TwinCAT.Ads;

namespace Dahlke.EtherCAT.Diagnostics.Tests;

/// <summary>
/// One raw operation as the channel received it, so a test can assert HOW a call was addressed
/// rather than only what it answered.
/// </summary>
internal sealed record RawCall(
    string AmsNetId, int Port, uint IndexGroup, uint IndexOffset, byte[] Data, TimeSpan? Timeout);

/// <summary>
/// An <see cref="IAdsRawChannelFactory"/> whose channels record every call and answer with one
/// caller-chosen exception.
///
/// <see cref="SimulatedRawChannelFixture"/> cannot cover this ground and is not a substitute:
/// <c>SimulatedRawConnection.WriteAsync</c> always succeeds, so a write's failure classification —
/// the whole point of surfacing an SDO abort code — has no way to run against the seeded store. It
/// also has no way to observe an abort code at all, because the simulation answers an unseeded slot
/// with <see cref="AdsErrorCode.DeviceInvalidOffset"/> and never with a slave's own abort.
///
/// Deliberately NOT a general fake of the library's retry, eviction or reconnection behaviour: it
/// is one throw or one recorded call, and the round-trip tests that need real transport behaviour
/// use the simulated fixture instead.
/// </summary>
internal sealed class ScriptedRawChannelFactory : IAdsRawChannelFactory
{
    private readonly Exception? _failWith;
    private readonly List<RawCall> _reads = [];
    private readonly List<RawCall> _writes = [];

    /// <param name="failWith">
    /// Thrown by every read and write. <see langword="null"/> records the call and succeeds — a
    /// write answers nothing and a read answers zero bytes.
    /// </param>
    internal ScriptedRawChannelFactory(Exception? failWith = null) => _failWith = failWith;

    /// <summary>Every <c>WriteAsync</c> this factory's channels received, in order.</summary>
    internal IReadOnlyList<RawCall> Writes => _writes;

    /// <summary>Every <c>ReadAsync</c> this factory's channels received, in order.</summary>
    internal IReadOnlyList<RawCall> Reads => _reads;

    public IAdsRawChannel Get(string amsNetId, int port) =>
        new ScriptedRawChannel(this, amsNetId, port);

    /// <summary>
    /// Always <see langword="false"/> — this factory is not the simulation, and a test that seeds
    /// through it would be seeding nothing.
    /// </summary>
    public bool TryGetSimulated(
        string amsNetId, int port, [NotNullWhen(true)] out ISimulatedRawChannel? simulated)
    {
        simulated = null;
        return false;
    }

    private sealed class ScriptedRawChannel : IAdsRawChannel
    {
        private readonly ScriptedRawChannelFactory _owner;

        internal ScriptedRawChannel(ScriptedRawChannelFactory owner, string amsNetId, int port)
        {
            _owner = owner;
            AmsNetId = amsNetId;
            Port = port;
        }

        public string AmsNetId { get; }

        public int Port { get; }

        public ConnectionState State => ConnectionState.Connected;

        public Task<int> ReadAsync(
            uint indexGroup, uint indexOffset, Memory<byte> destination, CancellationToken ct) =>
            ReadAsync(indexGroup, indexOffset, destination, timeout: null, ct);

        public Task<int> ReadAsync(
            uint indexGroup, uint indexOffset, Memory<byte> destination, TimeSpan timeout,
            CancellationToken ct) =>
            ReadAsync(indexGroup, indexOffset, destination, (TimeSpan?)timeout, ct);

        private Task<int> ReadAsync(
            uint indexGroup, uint indexOffset, Memory<byte> destination, TimeSpan? timeout,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _owner._reads.Add(new RawCall(
                AmsNetId, Port, indexGroup, indexOffset, new byte[destination.Length], timeout));

            return _owner._failWith is { } failure ? Task.FromException<int>(failure) : Task.FromResult(0);
        }

        public Task WriteAsync(
            uint indexGroup, uint indexOffset, ReadOnlyMemory<byte> source, CancellationToken ct) =>
            WriteAsync(indexGroup, indexOffset, source, timeout: null, ct);

        public Task WriteAsync(
            uint indexGroup, uint indexOffset, ReadOnlyMemory<byte> source, TimeSpan timeout,
            CancellationToken ct) =>
            WriteAsync(indexGroup, indexOffset, source, (TimeSpan?)timeout, ct);

        private Task WriteAsync(
            uint indexGroup, uint indexOffset, ReadOnlyMemory<byte> source, TimeSpan? timeout,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _owner._writes.Add(new RawCall(
                AmsNetId, Port, indexGroup, indexOffset, source.ToArray(), timeout));

            return _owner._failWith is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }

        public Task<int> ReadWriteAsync(
            uint indexGroup, uint indexOffset, Memory<byte> destination, ReadOnlyMemory<byte> source,
            CancellationToken ct) =>
            throw new NotSupportedException("No CoE path uses the ReadWrite service.");

        public Task<int> ReadWriteAsync(
            uint indexGroup, uint indexOffset, Memory<byte> destination, ReadOnlyMemory<byte> source,
            TimeSpan timeout, CancellationToken ct) =>
            throw new NotSupportedException("No CoE path uses the ReadWrite service.");

        public Task<StateInfo> ReadStateAsync(CancellationToken ct) =>
            throw new NotSupportedException("Only master discovery reads state, and it uses the simulation.");

        public Task<IDisposable> SubscribeAsync(
            uint indexGroup, uint indexOffset, int length, int cycleTimeMs,
            RawNotificationHandler handler, CancellationToken ct) =>
            throw new NotSupportedException("No CoE path subscribes.");
    }
}
