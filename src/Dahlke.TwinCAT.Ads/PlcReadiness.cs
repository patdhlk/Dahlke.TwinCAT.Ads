using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Whether one connection's PLC can serve symbol traffic: the liveness verdict behind
/// <see cref="AdsConnection.IsAliveAsync"/>, which gates both publishing Connected and the health loop.
/// </summary>
/// <remarks>
/// <para>
/// A ReadState answer alone is not enough. During an activation the runtime passes Config, Init and Start, and its
/// port can answer before the program and its symbol table are loaded; a connection published then hands every
/// re-registered subscription a "symbol not found" (0x710) and is never retried. So a PLC is ready only in Run or
/// Stop (a program is loaded) and with a readable symbol version.
/// </para>
/// <para>
/// The symbol version (index group 0xF008) changes with every download, activation and online change. Handles,
/// notification sizes and the cached symbol loader of this connection describe the old program, so a changed
/// version ends the connection: the pool reconnects, and the new connection re-registers every subscription
/// against the new table. The version is pinned by the first ready answer and never re-pinned; one instance
/// belongs to one connection.
/// </para>
/// </remarks>
internal sealed class PlcReadiness
{
    public enum Verdict
    {
        Ready,
        NotRunning,
        NoSymbolTable,
        SymbolTableChanged,
    }

    private byte? _pinned;

    public byte? PinnedSymbolVersion => _pinned;

    /// <param name="state">The device's ADS state.</param>
    /// <param name="symbolVersion">The symbol version, or null when it could not be read.</param>
    public Verdict Judge(AdsState state, byte? symbolVersion)
    {
        if (_pinned is { } pinned && symbolVersion is { } now && now != pinned)
            return Verdict.SymbolTableChanged;
        if (_pinned is not null && symbolVersion is null)
            return Verdict.NoSymbolTable;
        if (state is not (AdsState.Run or AdsState.Stop))
            return Verdict.NotRunning;
        if (symbolVersion is not { } version)
            return Verdict.NoSymbolTable;
        _pinned ??= version;
        return _pinned == version ? Verdict.Ready : Verdict.SymbolTableChanged;
    }
}
