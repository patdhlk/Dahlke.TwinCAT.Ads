namespace Dahlke.EtherCAT.Diagnostics;

/// <summary>
/// A diagnostics event the monitor emits. Every event concerns exactly one master, so
/// <see cref="MasterId"/> is enough for a consumer to route it without inspecting the concrete
/// type.
/// </summary>
/// <remarks>
/// This interface exists so change detection can return a typed collection. It previously returned
/// <c>IReadOnlyList&lt;object&gt;</c>, which is tolerable inside an application and not acceptable
/// on a library's public surface.
/// </remarks>
public interface IEtherCatEvent
{
    /// <summary>Device id of the master this event concerns.</summary>
    int MasterId { get; }
}
