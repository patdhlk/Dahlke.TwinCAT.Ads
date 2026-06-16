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
    // Members added in later tasks.
}
