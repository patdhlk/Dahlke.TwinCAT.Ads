using System.Collections.Concurrent;

namespace Dahlke.TwinCAT.Ads;

// The netstandard2.0 leg (#47) and the modern frameworks expose a few APIs in different shapes.
// Call sites throughout this project use ONE shape; this file is the only place that knows there
// are two implementations behind it.

#if NETSTANDARD2_0

/// <summary>
/// <c>TryRemove(KeyValuePair)</c> — remove only if the key still maps to exactly this value —
/// is a net5+ instance method. It wraps the explicit-interface <c>ICollection.Remove</c>, which
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> has always documented as that same atomic
/// conditional removal, so this extension gives netstandard2.0 the identical operation under the
/// identical name. On the modern frameworks the instance method wins overload resolution and
/// this type does not exist.
/// </summary>
internal static class ConcurrentDictionaryCompat
{
    public static bool TryRemove<TKey, TValue>(
        this ConcurrentDictionary<TKey, TValue> dictionary, KeyValuePair<TKey, TValue> item) =>
        ((ICollection<KeyValuePair<TKey, TValue>>)dictionary).Remove(item);
}

#else

/// <summary>
/// The TimeProvider-aware timing operations exist on netstandard2.0 only as
/// Microsoft.Bcl.TimeProvider's extension methods, whose receiver is the <see cref="TimeProvider"/>
/// itself — there is no way to add the net8+ static <c>Task.Delay(delay, timeProvider, ct)</c>
/// overload or the <c>CancellationTokenSource(delay, timeProvider)</c> constructor downlevel. So
/// call sites use the Bcl shape everywhere, and this type hands the modern frameworks that shape
/// by forwarding to their built-in APIs. On netstandard2.0 the Bcl package provides the real
/// thing and this type does not exist.
/// </summary>
internal static class TimeProviderCompat
{
    public static Task Delay(
        this TimeProvider timeProvider, TimeSpan delay, CancellationToken cancellationToken = default) =>
        Task.Delay(delay, timeProvider, cancellationToken);

    public static CancellationTokenSource CreateCancellationTokenSource(
        this TimeProvider timeProvider, TimeSpan delay) =>
        new(delay, timeProvider);
}

#endif
