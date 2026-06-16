using System.Reactive.Linq;
using Dahlke.TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Reactive;

/// <summary>
/// Rx (<see cref="IObservable{T}"/>) extensions over the callback-based
/// subscription and connection-state APIs of <see cref="IAdsConnection"/> and
/// <see cref="IAdsConnectionPool"/>.
/// </summary>
/// <remarks>
/// Value observables are <b>cold</b>: each <c>Subscribe</c> opens its own ADS
/// device notification, and disposing the subscription deletes it. Share a single
/// underlying notification with <c>.Publish().RefCount()</c>. Notifications arrive
/// on a background thread — add <c>.ObserveOn(...)</c> before updating UI.
/// </remarks>
public static class AdsReactiveExtensions
{
    /// <summary>
    /// Observes typed value changes for <paramref name="symbolPath"/> as a cold
    /// <see cref="IObservable{T}"/>. Each subscription opens its own ADS device
    /// notification; disposing the subscription deletes it. The stream is durable
    /// across reconnects (inherited from the underlying facade subscription).
    /// </summary>
    /// <typeparam name="T">The type each notification value is converted to. Values
    /// that cannot be converted (e.g. null into a non-nullable value type) are not
    /// emitted, matching <c>SubscribeAsync&lt;T&gt;</c>.</typeparam>
    public static IObservable<AdsValueChange<T>> ObserveValue<T>(
        this IAdsConnection connection, string symbolPath, int cycleTimeMs = 200)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(symbolPath);

        return Observable.Create<AdsValueChange<T>>((observer, ct) =>
            connection.SubscribeAsync<T>(
                symbolPath, cycleTimeMs,
                (symbol, value) => observer.OnNext(new AdsValueChange<T>(symbol, value)),
                ct));
    }

    /// <summary>
    /// Observes untyped (boxed) value changes for <paramref name="symbolPath"/> as a
    /// cold <see cref="IObservable{T}"/>. See <see cref="ObserveValue{T}"/> for the
    /// cold/durable/threading semantics.
    /// </summary>
    public static IObservable<AdsValueChange<object?>> ObserveValue(
        this IAdsConnection connection, string symbolPath, int cycleTimeMs = 200)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(symbolPath);

        return Observable.Create<AdsValueChange<object?>>((observer, ct) =>
            connection.SubscribeAsync(
                symbolPath, cycleTimeMs,
                (symbol, value) => observer.OnNext(new AdsValueChange<object?>(symbol, value)),
                ct));
    }
}
