namespace Dahlke.TwinCAT.Ads.Reactive;

/// <summary>
/// A single value-change notification from an ADS symbol subscription:
/// the fully-qualified <paramref name="Symbol"/> path and its new
/// <paramref name="Value"/> (already converted to <typeparamref name="T"/> for the
/// typed overload; the raw boxed value for the untyped overload, where
/// <typeparamref name="T"/> is <see cref="object"/>).
/// </summary>
/// <typeparam name="T">The value type the notification was projected to.</typeparam>
/// <param name="Symbol">The fully-qualified symbol path that changed.</param>
/// <param name="Value">The new value, or <see langword="null"/>.</param>
public readonly record struct AdsValueChange<T>(string Symbol, T? Value);
