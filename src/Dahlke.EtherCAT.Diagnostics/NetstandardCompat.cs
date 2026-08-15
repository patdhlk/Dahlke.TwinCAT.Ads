namespace Dahlke.EtherCAT.Diagnostics;

#if !NETSTANDARD2_0

/// <summary>
/// The TimeProvider-aware cancellation source exists on netstandard2.0 only as
/// Microsoft.Bcl.TimeProvider's <c>CreateCancellationTokenSource</c> extension method — there is
/// no way to add the net8+ <c>CancellationTokenSource(delay, timeProvider)</c> constructor
/// downlevel. So call sites use the Bcl shape everywhere, and this type hands the modern
/// frameworks that shape by forwarding to the constructor. On netstandard2.0 the Bcl package
/// provides the real thing and this type does not exist. Same pattern, same reasoning:
/// NetstandardCompat.cs in Dahlke.TwinCAT.Ads (internal there, hence repeated here).
/// </summary>
internal static class TimeProviderCompat
{
    public static CancellationTokenSource CreateCancellationTokenSource(
        this TimeProvider timeProvider, TimeSpan delay) =>
        new(delay, timeProvider);
}

#endif
