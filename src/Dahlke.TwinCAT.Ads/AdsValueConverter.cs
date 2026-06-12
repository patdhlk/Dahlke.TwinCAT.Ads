using System.Globalization;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Shared conversion core that turns a boxed runtime value into a requested
/// <typeparamref name="T"/>. The conversion rules are identical regardless of caller —
/// <see cref="System.Convert.ChangeType(object, Type, IFormatProvider)"/> with
/// <see cref="CultureInfo.InvariantCulture"/>, after a direct-cast fast path — so a typed
/// READ (<see cref="SimulatedAdsConnection.ReadValueAsync{T}"/>) and a typed NOTIFICATION
/// (<see cref="TypedCallbackAdapter"/>) interpret the same stored value the same way. The
/// two callers differ only in what they do on failure:
/// <list type="bullet">
///   <item><description>
///     The read path calls <see cref="ConvertForRead{T}"/>, which THROWS an actionable
///     <see cref="InvalidCastException"/> — the caller asked for a concrete type and a
///     conversion failure is a programming/data error worth surfacing.
///   </description></item>
///   <item><description>
///     The notification path calls <see cref="TryConvertForNotification{T}"/>, which DROPS
///     on failure (logs a Warning and returns <see langword="false"/>) — a single
///     unconvertible notification must not throw on the underlying ADS notification thread
///     and tear nothing down.
///   </description></item>
/// </list>
/// </summary>
internal static class AdsValueConverter
{
    /// <summary>
    /// Converts <paramref name="value"/> to <typeparamref name="T"/> for a typed READ,
    /// throwing an actionable <see cref="InvalidCastException"/> when the value cannot be
    /// converted. Mirrors the rules previously inlined in
    /// <see cref="SimulatedAdsConnection.ReadValueAsync{T}"/> exactly (no behaviour change):
    /// null + non-nullable value type throws; null + reference/nullable returns default;
    /// exact/assignable returns by cast; <see cref="IConvertible"/> uses
    /// <see cref="System.Convert.ChangeType(object, Type, IFormatProvider)"/> with
    /// <see cref="CultureInfo.InvariantCulture"/>; anything else throws.
    /// </summary>
    public static T ConvertForRead<T>(object? value, string symbolPath)
    {
        if (value is null)
        {
            var targetType = typeof(T);
            // For non-nullable value types null is illegal: there's nothing to return.
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
                throw new InvalidCastException(
                    $"Simulated symbol '{symbolPath}' has a null stored value; " +
                    $"cannot convert null to non-nullable value type '{targetType.Name}'.");

            // Reference type or Nullable<T>: null is a valid result.
            return default!;
        }

        // Exact type or assignable — fast path, no conversion needed.
        if (value is T directResult)
            return directResult;

        // IConvertible covers all primitives and string; supports numeric widening and
        // string-seeded values ("42"→int, "true"→bool, "3.14"→double).
        if (value is IConvertible)
        {
            try
            {
                var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                return (T)System.Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                throw new InvalidCastException(
                    $"Simulated symbol '{symbolPath}': cannot convert stored value " +
                    $"'{value}' (type: {value.GetType().Name}) to '{typeof(T).Name}'. {ex.Message}",
                    ex);
            }
        }

        // Non-IConvertible, not assignable: fail with an actionable message.
        throw new InvalidCastException(
            $"Simulated symbol '{symbolPath}': stored value has type '{value.GetType().Name}' " +
            $"which cannot be converted to requested type '{typeof(T).Name}'.");
    }

    /// <summary>
    /// Attempts to convert <paramref name="value"/> to <typeparamref name="T"/> for a typed
    /// NOTIFICATION, never throwing. Returns <see langword="true"/> with the converted value
    /// when conversion succeeds; returns <see langword="false"/> (and logs a Warning via
    /// <paramref name="logger"/>, when provided) when the notification should be DROPPED.
    /// </summary>
    /// <remarks>
    /// Drop cases:
    /// <list type="bullet">
    ///   <item><description>
    ///     <paramref name="value"/> is <see langword="null"/> and <typeparamref name="T"/> is
    ///     a non-nullable value type — there is no null to deliver.
    ///   </description></item>
    ///   <item><description>
    ///     <paramref name="value"/> cannot be converted to <typeparamref name="T"/>
    ///     (incompatible runtime type, failed <see cref="IConvertible"/> conversion).
    ///   </description></item>
    /// </list>
    /// Deliver-null case: <paramref name="value"/> is <see langword="null"/> and
    /// <typeparamref name="T"/> is a reference type or <see cref="Nullable{T}"/> — returns
    /// <see langword="true"/> with <paramref name="result"/> set to <see langword="null"/>.
    /// The successful conversions use the exact same code path as
    /// <see cref="ConvertForRead{T}"/>, so a value that a typed read would return is the same
    /// value a typed notification delivers.
    /// </remarks>
    public static bool TryConvertForNotification<T>(object? value, string symbolPath, ILogger? logger, out T? result)
    {
        if (value is null)
        {
            var targetType = typeof(T);
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
            {
                logger?.LogWarning(
                    "Dropping notification for {Symbol}: received a null value but the subscribed type " +
                    "'{Type}' is a non-nullable value type.",
                    symbolPath, targetType.Name);
                result = default;
                return false;
            }

            // Reference type or Nullable<T>: null is a valid delivered value.
            result = default;
            return true;
        }

        try
        {
            // Share the read path so conversion semantics stay identical between a typed
            // read and a typed notification. ConvertForRead throws on failure; we catch
            // and translate to the drop signal here.
            result = ConvertForRead<T>(value, symbolPath);
            return true;
        }
        catch (InvalidCastException ex)
        {
            logger?.LogWarning(
                ex,
                "Dropping notification for {Symbol}: value '{Value}' (type: {ActualType}) could not be " +
                "converted to the subscribed type '{Type}'.",
                symbolPath, value, value.GetType().Name, typeof(T).Name);
            result = default;
            return false;
        }
    }
}
