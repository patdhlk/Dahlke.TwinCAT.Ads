namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Bridges a typed subscription callback (<c>Action&lt;string, T?&gt;</c>) to the untyped
/// callback shapes the underlying subscription machinery speaks. Each connection implements only
/// the non-generic <c>SubscribeAsync</c> overloads; the generic overload wraps the caller's typed
/// callback with <see cref="WrapDecoded{T}"/> and registers it through the decoding
/// (<c>Action&lt;AdsNotification&gt;</c>) overload.
/// </summary>
/// <remarks>
/// Wrapping at the boundary is what makes typed subscriptions durable "for free": the
/// facade stores the already-wrapped callback in its durable record, so a
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
    /// <summary>
    /// Wraps <paramref name="callback"/> into an <c>Action&lt;AdsNotification&gt;</c> for the
    /// DECODING subscription overload — the one typed subscriptions register through. That overload
    /// delivers a struct, function block or array as the neutral tree
    /// <see cref="PlcTreeBinder"/> binds by member name; the untyped overload delivers it in
    /// Beckhoff's own shape (a <c>DynamicValue</c>), which no conversion to <typeparamref name="T"/>
    /// can use, so a typed subscription to a container registered there dropped every notification.
    /// Scalars arrive identically through both overloads.
    /// </summary>
    public static Action<AdsNotification> WrapDecoded<T>(Action<string, T?> callback, ILogger? logger)
    {
        var wrapped = Wrap(callback, logger);
        return notification => wrapped(notification.SymbolPath, notification.Value);
    }
}
