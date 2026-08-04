using System.Collections.Concurrent;
using Dahlke.EtherCAT.Diagnostics;

namespace Dahlke.EtherCAT.Diagnostics;

/// <summary>
/// Holds the most recent EtherCAT diagnostics per (PLC, master), plus whether the last poll cycle
/// for that master could read them at all.
///
/// <para>
/// <b>Only known-good snapshots are stored.</b> <see cref="Update"/> is called solely for a cycle
/// whose diagnostics reads all answered; a cycle that could not read them calls
/// <see cref="MarkDegraded"/> instead, which leaves the stored snapshot untouched. That is what
/// makes <see cref="GetSnapshot"/> the LAST KNOWN-GOOD reading rather than merely the latest one,
/// so change detection compares a new reading against the last thing the master actually said —
/// not against a placeholder invented for a dropped read.
/// </para>
/// </summary>
public interface IEtherCatCache
{
    /// <summary>
    /// All known-good snapshots for this PLC, one per master last read successfully. Empty when
    /// the PLC has never had a successful poll cycle.
    /// </summary>
    IReadOnlyList<EtherCatSnapshot> GetSnapshots(string plcId);

    /// <summary>The last snapshot built entirely from reads the master answered, or null if there
    /// has never been one for this master.</summary>
    EtherCatSnapshot? GetSnapshot(string plcId, int masterDeviceId);

    /// <summary>
    /// Whether at least one known-good snapshot has ever been stored for this PLC — true once any
    /// master on it has completed a successful poll cycle, even if every master is currently
    /// degraded.
    /// </summary>
    bool HasPlc(string plcId);

    /// <summary>Stores a known-good snapshot and clears this master's degraded marker.</summary>
    void Update(string plcId, int masterDeviceId, EtherCatSnapshot snapshot);

    /// <summary>
    /// Records that this master's diagnostics could not be read. The stored snapshot is left as
    /// it was, so callers keep serving the last known-good reading and can see it is stale.
    /// Idempotent.
    /// </summary>
    void MarkDegraded(string plcId, int masterDeviceId);

    /// <summary>
    /// Whether the most recent poll cycle for this master failed to read its diagnostics. False
    /// for a master that has never been polled.
    /// </summary>
    bool IsDegraded(string plcId, int masterDeviceId);
}

internal sealed class EtherCatCache : IEtherCatCache
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, EtherCatSnapshot>> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte>> _degraded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _knownPlcs = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<EtherCatSnapshot> GetSnapshots(string plcId)
    {
        if (_snapshots.TryGetValue(plcId, out var masters))
            return masters.Values.ToList();
        return [];
    }

    public EtherCatSnapshot? GetSnapshot(string plcId, int masterDeviceId)
    {
        if (_snapshots.TryGetValue(plcId, out var masters) && masters.TryGetValue(masterDeviceId, out var snapshot))
            return snapshot;
        return null;
    }

    public bool HasPlc(string plcId) => _knownPlcs.ContainsKey(plcId);

    public void Update(string plcId, int masterDeviceId, EtherCatSnapshot snapshot)
    {
        _knownPlcs.TryAdd(plcId, 0);
        var masters = _snapshots.GetOrAdd(plcId, _ => new ConcurrentDictionary<int, EtherCatSnapshot>());
        masters[masterDeviceId] = snapshot;

        if (_degraded.TryGetValue(plcId, out var degradedMasters))
            degradedMasters.TryRemove(masterDeviceId, out _);
    }

    public void MarkDegraded(string plcId, int masterDeviceId)
    {
        var degradedMasters = _degraded.GetOrAdd(plcId, _ => new ConcurrentDictionary<int, byte>());
        degradedMasters[masterDeviceId] = 0;
    }

    public bool IsDegraded(string plcId, int masterDeviceId) =>
        _degraded.TryGetValue(plcId, out var degradedMasters)
        && degradedMasters.ContainsKey(masterDeviceId);
}
