namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// What the pool's health check requires before it counts a target as connected.
/// </summary>
/// <remarks>
/// <para>
/// A PLC runtime can answer <c>ReadState</c> before its program and symbol table are loaded, so for a PLC an answer
/// alone is not enough (<see cref="PlcProgram"/>). Other ADS devices have no symbol table at all: the system service
/// (port 10000) rejects the symbol-version read with "service not supported" (0x701), the NC (500) and the I/O (300)
/// with "symbol not found" (0x710), the same code a PLC returns before its program is loaded. The answer cannot tell
/// the two apart, so the target says which rule applies.
/// </para>
/// </remarks>
public enum TargetReadiness
{
    /// <summary>
    /// <see cref="PlcProgram"/> on the PLC runtime ports 800-899 (TwinCAT 2 and 3), <see cref="DeviceState"/> on any
    /// other port.
    /// </summary>
    Auto,

    /// <summary>
    /// A PLC runtime: connected only in <c>Run</c> or <c>Stop</c> with a readable symbol version (index group
    /// <c>0xF008</c>), and reconnected when that version changes (activation, download, online change).
    /// </summary>
    PlcProgram,

    /// <summary>
    /// Any other ADS device (system service, NC, I/O, ...): connected while <c>ReadState</c> answers without an error,
    /// whatever the state.
    /// </summary>
    DeviceState,
}
