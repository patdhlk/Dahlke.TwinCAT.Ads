using System.Collections;
using System.Dynamic;
using System.Globalization;
using Microsoft.CSharp.RuntimeBinder;

namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// Turns one alarm-array notification value into <see cref="PlcAlarm"/> instances.
/// This is the ONLY place in the package that speaks <c>dynamic</c> or knows a PLC
/// member name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two shapes, one accessor.</b> A real ADS notification decodes to Beckhoff's own
/// object graph, where members are reached dynamically. The simulated connection
/// carries a plain dictionary tree. Both are real deployment targets — the example
/// and most tests run simulated — so member access tries
/// <see cref="IDictionary"/> first and falls back to dynamic binding.
/// </para>
/// <para>
/// <b>It throws.</b> A missing or wrongly-typed member raises
/// <see cref="PlcAlarmShapeException"/> naming the member and the symbol path. The one
/// tolerated deviation is an <c>ErrorType</c> outside the known set, which is preserved
/// and logged — an unrecognised severity is still a real alarm and must not vanish.
/// </para>
/// </remarks>
internal static class PlcAlarmBinder
{
    // PLC member names, verbatim from ST_ErrorEntry and TIMESTRUCT.
    private const string MemberKey = "sKey";
    private const string MemberId = "Id";
    private const string MemberErrorCode = "ErrorCode";
    private const string MemberErrorType = "ErrorType";
    private const string MemberIsActive = "IsActive";
    private const string MemberNeedsAck = "NeedsAck";
    private const string MemberIsAcked = "IsAcked";
    private const string MemberTimestamp = "PLCTimeStamp";

    private const string TimeYear = "wYear";
    private const string TimeMonth = "wMonth";
    private const string TimeDay = "wDay";
    private const string TimeHour = "wHour";
    private const string TimeMinute = "wMinute";
    private const string TimeSecond = "wSecond";
    private const string TimeMillisecond = "wMilliseconds";

    /// <summary>
    /// Binds <paramref name="notificationValue"/> — one whole alarm array — into alarms,
    /// with each element's array position as its <see cref="PlcAlarm.SlotIndex"/>.
    /// </summary>
    /// <exception cref="PlcAlarmShapeException">
    /// The value is not an array, or an element is missing a member or has one of the
    /// wrong type.
    /// </exception>
    public static IReadOnlyList<PlcAlarm> Bind(
        object? notificationValue, string plcId, string symbolPath,
        PlcClockKind clock, ILogger? logger)
    {
        if (notificationValue is null)
            return [];

        if (notificationValue is not IEnumerable elements || notificationValue is string)
            throw new PlcAlarmShapeException(
                $"Alarm symbol '{symbolPath}' on target '{plcId}' produced a " +
                $"{notificationValue.GetType().Name} where an array of alarm entries was " +
                "expected. Point PlcAlarms:Targets:" + plcId + ":SymbolPath at the alarm array.");

        var alarms = new List<PlcAlarm>();
        var slot = 0;

        foreach (var element in elements)
        {
            alarms.Add(BindEntry(element, plcId, symbolPath, slot, clock, logger));
            slot++;
        }

        return alarms;
    }

    private static PlcAlarm BindEntry(
        object? element, string plcId, string symbolPath, int slot,
        PlcClockKind clock, ILogger? logger)
    {
        var context = $"{symbolPath}[{slot}]";

        return new PlcAlarm
        {
            Key = Read<string>(element, MemberKey, context, plcId) ?? string.Empty,
            EquipmentId = Read<string>(element, MemberId, context, plcId) ?? string.Empty,
            ErrorCode = Read<uint>(element, MemberErrorCode, context, plcId),
            Severity = BindSeverity(element, context, plcId, logger),
            IsActive = Read<bool>(element, MemberIsActive, context, plcId),
            NeedsAcknowledgement = Read<bool>(element, MemberNeedsAck, context, plcId),
            IsAcknowledged = Read<bool>(element, MemberIsAcked, context, plcId),
            PlcTimestamp = BindTimestamp(element, context, plcId, clock),
            SlotIndex = slot,
            PlcId = plcId,
        };
    }

    private static AlarmSeverity BindSeverity(
        object? element, string context, string plcId, ILogger? logger)
    {
        var raw = Read<int>(element, MemberErrorType, context, plcId);

        if (!Enum.IsDefined(typeof(AlarmSeverity), raw))
        {
            logger?.LogWarning(
                "Alarm entry {Context} on target {PlcId} reported ErrorType {Value}, which is not a " +
                "known E_ErrorType value. The alarm is kept with its raw severity — check that " +
                "E_ErrorType still numbers None=0, Info=1, Warning=2, Error=3.",
                context, plcId, raw);
        }

        return (AlarmSeverity)raw;
    }

    private static DateTime BindTimestamp(
        object? element, string context, string plcId, PlcClockKind clock)
    {
        var time = ReadMember(element, MemberTimestamp, context, plcId);

        var year = Read<ushort>(time, TimeYear, context, plcId);
        var month = Read<ushort>(time, TimeMonth, context, plcId);
        var day = Read<ushort>(time, TimeDay, context, plcId);

        // A zeroed struct is an uninitialised entry, not a broken contract.
        if (year == 0 || month == 0 || day == 0)
            return default;

        var hour = Read<ushort>(time, TimeHour, context, plcId);
        var minute = Read<ushort>(time, TimeMinute, context, plcId);
        var second = Read<ushort>(time, TimeSecond, context, plcId);
        var millisecond = Read<ushort>(time, TimeMillisecond, context, plcId);

        var kind = clock switch
        {
            PlcClockKind.Utc => DateTimeKind.Utc,
            PlcClockKind.Local => DateTimeKind.Local,
            _ => DateTimeKind.Unspecified,
        };

        try
        {
            return new DateTime(year, month, day, hour, minute, second, millisecond, kind);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new PlcAlarmShapeException(
                $"Alarm entry {context} on target '{plcId}' carries an out-of-range " +
                $"{MemberTimestamp} ({year:D4}-{month:D2}-{day:D2} {hour:D2}:{minute:D2}:{second:D2}). " +
                "Check the PLC's clock and the TIMESTRUCT member order.", ex);
        }
    }

    /// <summary>
    /// Reads <paramref name="member"/> and converts it to <typeparamref name="T"/>,
    /// throwing <see cref="PlcAlarmShapeException"/> when it is absent or not convertible.
    /// </summary>
    private static T Read<T>(object? source, string member, string context, string plcId)
    {
        var value = ReadMember(source, member, context, plcId);

        if (value is T typed)
            return typed;

        if (value is null)
        {
            if (default(T) is null)
                return default!;

            throw new PlcAlarmShapeException(
                $"Alarm entry {context} on target '{plcId}' has a null '{member}' where a " +
                $"{typeof(T).Name} was expected.");
        }

        try
        {
            return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            throw new PlcAlarmShapeException(
                $"Alarm entry {context} on target '{plcId}' has a '{member}' of type " +
                $"{value.GetType().Name}, which cannot be read as {typeof(T).Name}. The PLC's " +
                "ST_ErrorEntry no longer matches what this package binds.", ex);
        }
    }

    /// <summary>
    /// Reads one member from either shape — a dictionary tree (simulation) or a dynamic
    /// object (real ADS notification payload).
    /// </summary>
    private static object? ReadMember(object? source, string member, string context, string plcId)
    {
        if (source is null)
            throw new PlcAlarmShapeException(
                $"Alarm entry {context} on target '{plcId}' is null where an alarm entry was expected.");

        if (source is IDictionary<string, object?> typedMap)
        {
            // NOTE: ExpandoObject implements IDictionary<string, object> under the hood,
            // and nullable-reference annotations erase at runtime, so ExpandoObject
            // instances land HERE rather than in ReadDynamicMember below. Real ADS
            // dynamic payloads (e.g. Beckhoff's DynamicValue) are not IDictionary and do
            // take the dynamic branch — see ReadDynamicMember and DynamicMemberNames.
            if (typedMap.TryGetValue(member, out var value))
                return value;

            // ExpandoObject and neutral trees may be built case-sensitively.
            foreach (var pair in typedMap)
            {
                if (string.Equals(pair.Key, member, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }

            throw Missing(member, context, plcId, typedMap.Keys);
        }

        if (source is IDictionary map)
        {
            foreach (DictionaryEntry entry in map)
            {
                if (entry.Key is string key && string.Equals(key, member, StringComparison.OrdinalIgnoreCase))
                    return entry.Value;
            }

            throw Missing(member, context, plcId, map.Keys.Cast<object>().Select(k => k?.ToString() ?? ""));
        }

        return ReadDynamicMember(source, member, context, plcId);
    }

    /// <summary>
    /// Reads <paramref name="member"/> off a genuine dynamic object via a DLR
    /// <see cref="System.Runtime.CompilerServices.CallSite"/>, matching the exact
    /// spelling first. On a miss, retries once against whichever of
    /// <see cref="DynamicMemberNames"/> is a case-insensitive match for
    /// <paramref name="member"/> — the package's member lookup is case-insensitive
    /// everywhere, and the dictionary branch above already honours that; a dynamic
    /// object that disagreed with its own dictionary-shaped sibling on casing would be
    /// a latent bug, not a stylistic wrinkle. A miss on both the exact name and (if
    /// found) the retried spelling produces the same <see cref="Missing"/> diagnostic.
    /// </summary>
    private static object? ReadDynamicMember(object source, string member, string context, string plcId)
    {
        try
        {
            return InvokeGetMember(source, member);
        }
        catch (RuntimeBinderException ex)
        {
            var available = DynamicMemberNames(source).ToList();

            // Retry once, but only when exactly one present member matches
            // case-insensitively — an ambiguous match (e.g. both "sKey" and "SKEY"
            // present) is not this package's call to arbitrate, so it falls through
            // to the ordinary missing-member diagnostic instead.
            var caseInsensitiveMatches = available
                .Where(name => string.Equals(name, member, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (caseInsensitiveMatches.Count == 1)
            {
                try
                {
                    return InvokeGetMember(source, caseInsensitiveMatches[0]);
                }
                catch (RuntimeBinderException)
                {
                    // Fall through to the same Missing diagnostic as a straight miss.
                }
            }

            throw Missing(member, context, plcId, available, ex);
        }
    }

    private static object? InvokeGetMember(object source, string member)
    {
        var binder = Microsoft.CSharp.RuntimeBinder.Binder.GetMember(
            CSharpBinderFlags.None, member, typeof(PlcAlarmBinder),
            [CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.None, null)]);

        var site = System.Runtime.CompilerServices.CallSite<Func<
            System.Runtime.CompilerServices.CallSite, object, object?>>.Create(binder);

        return site.Target(site, source);
    }

    private static IEnumerable<string> DynamicMemberNames(object source) =>
        source is IDynamicMetaObjectProvider provider
            ? provider.GetMetaObject(System.Linq.Expressions.Expression.Constant(source))
                .GetDynamicMemberNames()
            : source.GetType().GetProperties().Select(p => p.Name);

    private static PlcAlarmShapeException Missing(
        string member, string context, string plcId,
        IEnumerable<string> available, Exception? inner = null)
    {
        var message =
            $"Alarm entry {context} on target '{plcId}' has no member '{member}'. " +
            $"Members present: {string.Join(", ", available.OrderBy(n => n, StringComparer.Ordinal))}. " +
            "This package binds the PLC's ST_ErrorEntry (sKey, Id, ErrorCode, ErrorType, " +
            "IsActive, NeedsAck, IsAcked, PLCTimeStamp) — check that SymbolPath points at the " +
            "alarm array and that the PLC type has not been renamed.";

        return inner is null
            ? new PlcAlarmShapeException(message)
            : new PlcAlarmShapeException(message, inner);
    }
}
