namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Bridges a typed subscription callback (<c>Action&lt;string, T?&gt;</c>) to the untyped
/// callback shape (<c>Action&lt;string, object?&gt;</c>) the underlying subscription
/// machinery speaks. Each connection implements only the untyped
/// <c>SubscribeAsync</c>; the generic overload wraps the caller's typed callback with
/// <see cref="Wrap{T}"/> and registers the resulting untyped delegate.
/// </summary>
/// <remarks>
/// Wrapping at the boundary is what makes typed subscriptions durable "for free": the
/// facade stores the already-wrapped untyped callback in its durable record, so a
/// reconnect re-registers the same wrapped delegate without the facade ever needing to
/// know the subscription was typed. Conversion happens inside the wrapper on every
/// notification, on the underlying ADS notification thread.
/// </remarks>
internal static class TypedCallbackAdapter
{
    /// <summary>
    /// Wraps <paramref name="callback"/> into an <c>Action&lt;string, object?&gt;</c> that
    /// converts each notification value to <typeparamref name="T"/> using
    /// <see cref="AdsValueConverter.TryConvertForNotification{T}"/>. When conversion
    /// succeeds the typed callback is invoked; when it fails the notification is dropped
    /// (a Warning is logged via <paramref name="logger"/>) and the callback is not invoked.
    /// </summary>
    public static Action<string, object?> Wrap<T>(Action<string, T?> callback, ILogger? logger)
        => (path, value) =>
        {
            if (AdsValueConverter.TryConvertForNotification<T>(value, path, logger, out var converted))
                callback(path, converted);
        };
}
