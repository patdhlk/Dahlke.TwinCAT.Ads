using System.Collections;
using System.Collections.Concurrent;
using System.Dynamic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.CSharp.RuntimeBinder;
using TwinCAT.TypeSystem;

namespace Dahlke.TwinCAT.Ads.Alarms;

/// <summary>
/// Turns one alarm-array notification value into <see cref="PlcAlarm"/> instances.
/// This is the ONLY place in the package that speaks <c>dynamic</c>, and the only place
/// that READS a PLC member. It is not quite the only place that names one:
/// <c>PlcAlarmMonitor.AcknowledgeAsync</c> writes acknowledgements through the symbol
/// paths <c>…[i].sKey</c> and <c>…[i].IsAcked</c>, which must stay in step with
/// <see cref="MemberKey"/> and <see cref="MemberIsAcked"/> here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four shapes, one accessor.</b> The simulated connection carries a plain
/// dictionary tree (<see cref="IDictionary{TKey, TValue}"/> or non-generic
/// <see cref="IDictionary"/>). A real ADS notification decodes to Beckhoff's own
/// <c>DynamicArrayValue</c> / <c>DynamicValue</c> object graph, which is reached
/// through <see cref="IArrayValue"/> at the array level and <see cref="IStructValue"/>
/// at the member level — NOT through <see cref="IEnumerable"/>, which
/// <c>DynamicArrayValue</c> does not implement. A residual DLR dynamic-binding
/// fallback exists for any other genuine dynamic object (see
/// <see cref="ReadDynamicMember"/>). All four are real deployment or test shapes; see
/// <see cref="ReadMember"/> and <c>Bind</c> for the routing order.
/// </para>
/// <para>
/// <b>It throws.</b> A missing or wrongly-typed member raises
/// <see cref="PlcAlarmShapeException"/> naming the member and the symbol path. The one
/// tolerated deviation is an <c>ErrorType</c> outside the known set, which is preserved
/// and logged once per distinct value — an unrecognised severity is still a real alarm
/// and must not vanish.
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
    /// The only CLR types a PLC numeric member may legitimately arrive as. Used by
    /// <see cref="TryConvertLosslessly{T}"/> to keep a string or a bool from ever being
    /// coerced into a number, and a number from ever being coerced into a bool.
    /// </summary>
    private static readonly HashSet<Type> IntegralTypes =
    [
        typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
        typeof(int), typeof(uint), typeof(long), typeof(ulong),
    ];

    /// <summary>
    /// One DLR <see cref="CallSite{T}"/> per distinct member NAME, reused across every
    /// entry and every notification. A dynamic member read normally compiles its own
    /// call site per call; on a 100-slot alarm array read at PLC cycle rate that would
    /// mean roughly 1,500 freshly compiled call sites per notification, on the ADS
    /// notification thread, with the DLR's own polymorphic inline caching defeated
    /// because nothing is ever reused. Keying by member name — not by
    /// (member, runtime type) — is deliberate: the DLR's caching is designed to be
    /// reused across differently-typed targets at the SAME call site, which is exactly
    /// this scenario (a dictionary-tree entry, an <c>ExpandoObject</c>, and a real
    /// Beckhoff dynamic object never appear at the same call site in one process, but
    /// if they did, this still binds correctly per target type).
    /// </summary>
    private static readonly ConcurrentDictionary<string, CallSite<Func<CallSite, object, object?>>> MemberGetSites = new();

    /// <summary>
    /// Which (target, raw severity) pairs have already logged the unknown-<c>ErrorType</c>
    /// warning. <see cref="PlcAlarm.Severity"/>'s own doc promises "logged once"; a stuck
    /// unknown value read every notification at cycle rate would otherwise flood the log
    /// forever. Keyed by target as well as value, since two different PLC targets each
    /// reporting the same unrecognised raw number are two separate facts an operator needs
    /// to see, not one.
    /// </summary>
    private static readonly ConcurrentDictionary<(string PlcId, int RawSeverity), byte> ReportedUnknownSeverities = new();

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

        IEnumerable elements;

        if (notificationValue is IArrayValue arrayValue && arrayValue.TryGetArrayElementValues(out var beckhoffElements))
        {
            // Beckhoff's own array shape (DynamicArrayValue and friends) is NOT
            // IEnumerable itself, so this has to be tried FIRST — the IEnumerable branch
            // below would simply never match it.
            elements = beckhoffElements;
        }
        else if (notificationValue is IEnumerable enumerable
            && notificationValue is not IDictionary
            && notificationValue is not IDictionary<string, object?>
            && notificationValue is not string)
        {
            // A plain array/list of entries — what the simulated connection carries.
            // Dictionaries are explicitly excluded even though Dictionary and
            // ExpandoObject are both IEnumerable<KeyValuePair<...>>: pointing
            // SymbolPath at a single alarm entry instead of the array would otherwise
            // silently enumerate that entry's KeyValuePairs as if they were alarm
            // entries, producing "Members present: Key, Value" instead of the clear
            // non-array message below.
            elements = enumerable;
        }
        else
        {
            throw new PlcAlarmShapeException(
                $"Alarm symbol '{symbolPath}' on target '{plcId}' produced a " +
                $"{notificationValue.GetType().Name} where an array of alarm entries was " +
                "expected. Point PlcAlarms:Targets:" + plcId + ":SymbolPath at the alarm array.");
        }

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
            // "Logged once" (PlcAlarm.Severity's doc) means once per DISTINCT unknown
            // value per target, not once ever and not once per notification. Same latch
            // idiom as AdsConnection.GetNotificationValue's fallbackReported — an atomic
            // "first writer wins" check — but keyed, since which values are unknown is a
            // runtime fact discovered per target rather than the one fixed condition
            // that method latches.
            if (ReportedUnknownSeverities.TryAdd((plcId, raw), 0))
            {
                logger?.LogWarning(
                    "Alarm entry {Context} on target {PlcId} reported ErrorType {Value}, which is not a " +
                    "known E_ErrorType value. The alarm is kept with its raw severity — check that " +
                    "E_ErrorType still numbers None=0, Info=1, Warning=2, Error=3. This is logged once " +
                    "per distinct value per target.",
                    context, plcId, raw);
            }
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
    /// throwing <see cref="PlcAlarmShapeException"/> when it is absent or not
    /// convertible. Conversion is deliberately narrow — see
    /// <see cref="TryConvertLosslessly{T}"/> — because the widest, most convenient
    /// conversion (<see cref="Convert.ChangeType(object?, Type)"/> alone) is exactly
    /// what let the mismatches most likely to occur pass silently: a PLC retyping
    /// <c>sKey</c> to a number used to bind <c>Key = "404"</c> with no error at all,
    /// silently re-keying the whole outstanding alarm set, since <c>Key</c> is the
    /// alarm's identity in the store.
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

        if (TryConvertLosslessly<T>(value, out var converted))
            return converted;

        throw new PlcAlarmShapeException(
            $"Alarm entry {context} on target '{plcId}' has a '{member}' of type " +
            $"{value.GetType().Name}, which cannot be read as {typeof(T).Name}. The PLC's " +
            "ST_ErrorEntry no longer matches what this package binds.");
    }

    /// <summary>
    /// Converts <paramref name="value"/> to <typeparamref name="T"/> only when that is a
    /// lossless conversion between two INTEGRAL numeric types. <see cref="string"/> and
    /// <see cref="bool"/> targets always return <see langword="false"/> here — never
    /// coerced from anything — because <see cref="Convert.ChangeType(object?, Type)"/>
    /// converts any <see cref="IConvertible"/> number to <see langword="bool"/> and
    /// formats anything as a string, which is precisely the silent-mismatch behaviour
    /// this method replaces.
    /// </summary>
    private static bool TryConvertLosslessly<T>(object value, out T converted)
    {
        converted = default!;

        if (typeof(T) == typeof(string) || typeof(T) == typeof(bool))
            return false;

        if (!IntegralTypes.Contains(value.GetType()))
            return false;

        try
        {
            var candidate = (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);

            // Convert.ChangeType's own checked conversions already reject most
            // information loss (e.g. a too-large uint overflowing int), but round-
            // tripping back to the source type is the explicit, self-documenting
            // check that this really was lossless rather than relying on that being an
            // implementation detail of every type pair.
            var roundTripped = Convert.ChangeType(candidate, value.GetType(), CultureInfo.InvariantCulture);
            if (!roundTripped.Equals(value))
                return false;

            converted = candidate;
            return true;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads one member from whichever of the four shapes <paramref name="source"/> is
    /// — see the class remarks for the routing order and why each branch exists.
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
            // dynamic payloads (Beckhoff's DynamicValue) are not IDictionary and do
            // take the IStructValue or dynamic branch below instead.
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

        if (source is IStructValue structValue)
            return ReadStructValueMember(structValue, source, member, context, plcId);

        return ReadDynamicMember(source, member, context, plcId);
    }

    /// <summary>
    /// Reads <paramref name="member"/> off Beckhoff's own <see cref="IStructValue"/> —
    /// the shape a real ADS notification's struct members (an alarm entry, or its
    /// nested <c>PLCTimeStamp</c>) actually arrive as.
    /// </summary>
    /// <remarks>
    /// <b>Reserved-name hazard.</b> See
    /// <c>src/Dahlke.TwinCAT.Ads/NotificationPayload.cs</c>, which documents this from
    /// the read side: <see cref="IStructValue.TryGetMemberValue"/> does not look a
    /// member up at all when its name collides with one of <c>DynamicValue</c>'s own
    /// public properties — <c>Symbol</c>, <c>DataType</c>, <c>TimeStamp</c>, <c>Age</c>,
    /// <c>CachedRaw</c>, <c>CachedRawStatic</c>, <c>IsPrimitive</c>, <c>UpdateMode</c>,
    /// <c>ParentValue</c>, <c>ValueFactory</c>, <c>RootValue</c> — and throws
    /// <see cref="KeyNotFoundException"/> instead. Checked against this package's two
    /// bound structs: <c>ST_ErrorEntry</c> (<c>sKey</c>, <c>Id</c>, <c>ErrorCode</c>,
    /// <c>ErrorType</c>, <c>IsActive</c>, <c>NeedsAck</c>, <c>IsAcked</c>,
    /// <c>PLCTimeStamp</c>) and <c>TIMESTRUCT</c> (<c>wYear</c>…<c>wMilliseconds</c>)
    /// collide with NONE of them, so this binder is safe. Anyone extending it to
    /// another PLC struct must check that struct's member names against the list above
    /// before assuming the same — the <see cref="KeyNotFoundException"/> catch below
    /// exists only so a future collision still fails as a
    /// <see cref="PlcAlarmShapeException"/> naming the cause, rather than an opaque
    /// framework exception, should that check ever be missed.
    /// </remarks>
    private static object? ReadStructValueMember(
        IStructValue structValue, object source, string member, string context, string plcId)
    {
        try
        {
            if (structValue.TryGetMemberValue(member, out var value))
                return value;
        }
        catch (KeyNotFoundException ex)
        {
            throw new PlcAlarmShapeException(
                $"Alarm entry {context} on target '{plcId}' could not read '{member}': it collides " +
                "with one of DynamicValue's own reserved property names (Symbol, DataType, " +
                "TimeStamp, Age, CachedRaw, CachedRawStatic, IsPrimitive, UpdateMode, ParentValue, " +
                "ValueFactory, RootValue) and Beckhoff's IStructValue.TryGetMemberValue does not " +
                "look such a name up at all. This PLC struct needs a member rename or a different " +
                "binding approach.", ex);
        }

        // TryGetMemberValue is exact-match only; retry once, exactly as ReadDynamicMember
        // does, against a single unambiguous case-insensitive match.
        var available = DynamicMemberNames(source).ToList();
        var matches = available
            .Where(name => string.Equals(name, member, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            try
            {
                if (structValue.TryGetMemberValue(matches[0], out var retried))
                    return retried;
            }
            catch (KeyNotFoundException)
            {
                // The exact-spelling attempt above already reported the collision
                // hazard if THAT name collided; a retried spelling colliding instead
                // is not this package's call to diagnose further, so fall through.
            }
        }

        throw Missing(member, context, plcId, available);
    }

    /// <summary>
    /// Reads <paramref name="member"/> off a genuine dynamic object via a cached DLR
    /// <see cref="CallSite{T}"/> (see <see cref="MemberGetSites"/>), matching the exact
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

    /// <summary>
    /// Invokes a cached "get member" DLR call site for <paramref name="member"/> against
    /// <paramref name="source"/>. The call site is built once per distinct member name
    /// and reused forever after — see <see cref="MemberGetSites"/> — rather than built
    /// fresh (and thrown away) on every single read, which would compile a new
    /// expression tree per member read and defeat the DLR's own caching entirely.
    /// </summary>
    private static object? InvokeGetMember(object source, string member)
    {
        var site = MemberGetSites.GetOrAdd(member, static name =>
        {
            var binder = Microsoft.CSharp.RuntimeBinder.Binder.GetMember(
                CSharpBinderFlags.None, name, typeof(PlcAlarmBinder),
                [CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.None, null)]);

            return CallSite<Func<CallSite, object, object?>>.Create(binder);
        });

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
