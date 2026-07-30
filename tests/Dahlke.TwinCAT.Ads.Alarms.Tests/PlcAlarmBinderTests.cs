using System.Dynamic;
using Microsoft.Extensions.Logging;
using TwinCAT.TypeSystem;

namespace Dahlke.TwinCAT.Ads.Alarms.Tests;

/// <summary>
/// Unit tests for <see cref="PlcAlarmBinder"/>.
/// </summary>
/// <remarks>
/// Every behavioural test runs against every shape the binder will really meet: the
/// dictionary tree the simulated connection carries, an <see cref="ExpandoObject"/>, a
/// true dynamic object with no <see cref="System.Collections.IDictionary"/> in sight,
/// and Beckhoff's own <see cref="IArrayValue"/> / <see cref="IStructValue"/> shape —
/// the one every real ADS notification actually takes, since <c>DynamicArrayValue</c>
/// is NOT <see cref="IEnumerable{T}"/> and its members are NOT reached through the
/// plain DLR dynamic fallback. <see cref="ExpandoObject"/> is a trap worth naming:
/// it implements <see cref="IDictionary{TKey, TValue}"/> under the hood, and nullable
/// annotations erase at runtime, so it is caught by PlcAlarmBinder's dictionary
/// branch, not its DLR dynamic-binding branch. Only <see cref="TrueDynamicEntry"/>
/// forces a read through that branch. The original branch tested only a shape its own
/// tests invented, which is why it could be green while wrong; testing
/// <see cref="ExpandoObject"/> alone here — or the DLR fallback alone, when real
/// hardware never reaches it — would have repeated that mistake with better manners.
/// </remarks>
public class PlcAlarmBinderTests
{
    private const string PlcId = "plc1";
    private const string Path = "GVL.Errors";

    /// <summary>Builds one entry as the SIMULATED connection carries it.</summary>
    private static Dictionary<string, object?> AsDictionary(
        string sKey = "BMK1Err404", string id = "BMK1", uint errorCode = 404,
        int errorType = 3, bool isActive = true, bool needsAck = true, bool isAcked = false,
        ushort year = 2026, ushort month = 6, ushort day = 17) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sKey"] = sKey,
            ["Id"] = id,
            ["ErrorCode"] = errorCode,
            ["ErrorType"] = errorType,
            ["IsActive"] = isActive,
            ["NeedsAck"] = needsAck,
            ["IsAcked"] = isAcked,
            ["PLCTimeStamp"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["wYear"] = year, ["wMonth"] = month, ["wDayOfWeek"] = (ushort)3, ["wDay"] = day,
                ["wHour"] = (ushort)12, ["wMinute"] = (ushort)0, ["wSecond"] = (ushort)0,
                ["wMilliseconds"] = (ushort)0,
            },
        };

    /// <summary>Builds the same entry as a DYNAMIC object, as real hardware delivers.</summary>
    private static dynamic AsDynamic(
        string sKey = "BMK1Err404", string id = "BMK1", uint errorCode = 404,
        int errorType = 3, bool isActive = true, bool needsAck = true, bool isAcked = false,
        ushort year = 2026, ushort month = 6, ushort day = 17)
    {
        dynamic time = new ExpandoObject();
        time.wYear = year; time.wMonth = month; time.wDayOfWeek = (ushort)3; time.wDay = day;
        time.wHour = (ushort)12; time.wMinute = (ushort)0; time.wSecond = (ushort)0;
        time.wMilliseconds = (ushort)0;

        dynamic entry = new ExpandoObject();
        entry.sKey = sKey; entry.Id = id; entry.ErrorCode = errorCode; entry.ErrorType = errorType;
        entry.IsActive = isActive; entry.NeedsAck = needsAck; entry.IsAcked = isAcked;
        entry.PLCTimeStamp = time;
        return entry;
    }

    /// <summary>
    /// A genuine dynamic object — deliberately NOT an <see cref="IDictionary"/> —
    /// standing in for Beckhoff's own <c>DynamicValue</c>, the shape a real ADS
    /// notification payload actually decodes to. Unlike <see cref="ExpandoObject"/>,
    /// this type has no dictionary interface for PlcAlarmBinder to catch, so every
    /// read is forced through its DLR <c>CallSite</c> dynamic-binding branch.
    /// </summary>
    private sealed class TrueDynamicEntry : DynamicObject
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public void Set(string name, object? value) => _values[name] = value;

        public void Remove(string name) => _values.Remove(name);

        public override bool TryGetMember(GetMemberBinder binder, out object? result) =>
            _values.TryGetValue(binder.Name, out result);

        public override IEnumerable<string> GetDynamicMemberNames() => _values.Keys;
    }

    /// <summary>Builds the same entry as a TRUE dynamic object — see <see cref="TrueDynamicEntry"/>.</summary>
    private static TrueDynamicEntry AsTrueDynamic(
        string sKey = "BMK1Err404", string id = "BMK1", uint errorCode = 404,
        int errorType = 3, bool isActive = true, bool needsAck = true, bool isAcked = false,
        ushort year = 2026, ushort month = 6, ushort day = 17)
    {
        var time = new TrueDynamicEntry();
        time.Set("wYear", year); time.Set("wMonth", month); time.Set("wDayOfWeek", (ushort)3); time.Set("wDay", day);
        time.Set("wHour", (ushort)12); time.Set("wMinute", (ushort)0); time.Set("wSecond", (ushort)0);
        time.Set("wMilliseconds", (ushort)0);

        var entry = new TrueDynamicEntry();
        entry.Set("sKey", sKey); entry.Set("Id", id); entry.Set("ErrorCode", errorCode); entry.Set("ErrorType", errorType);
        entry.Set("IsActive", isActive); entry.Set("NeedsAck", needsAck); entry.Set("IsAcked", isAcked);
        entry.Set("PLCTimeStamp", time);
        return entry;
    }

    /// <summary>
    /// A fake standing in for Beckhoff's own struct value (<c>DynamicValue</c>) for a
    /// single alarm entry or its nested <c>PLCTimeStamp</c>. Real ADS struct values
    /// reach PlcAlarmBinder through <see cref="IStructValue.TryGetMemberValue"/>,
    /// which — unlike the DLR dynamic fallback — is EXACT-MATCH only, so this fake is
    /// deliberately exact-match too. Derives from <see cref="DynamicObject"/> purely so
    /// <see cref="GetDynamicMemberNames"/> is reachable the same way real
    /// <c>DynamicValue</c> exposes it (it is itself a <see cref="DynamicObject"/>
    /// subclass), which is what makes the case-insensitive retry and the "members
    /// present" diagnostic work on this branch at all.
    /// </summary>
    private sealed class BeckhoffStructValue : DynamicObject, IStructValue
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public void Set(string name, object? value) => _values[name] = value;

        public void Remove(string name) => _values.Remove(name);

        public bool TryGetMemberValue(string name, out object value)
        {
            var found = _values.TryGetValue(name, out var raw);
            value = raw!;
            return found;
        }

        public bool TrySetMemberValue(string name, object? value) => throw new NotSupportedException();

        public override IEnumerable<string> GetDynamicMemberNames() => _values.Keys;

        // IValue members PlcAlarmBinder never touches for a struct-level node.
        public ISymbol Symbol => throw new NotSupportedException();
        public IDataType DataType => throw new NotSupportedException();
        public ReadOnlyMemory<byte> CachedRaw => throw new NotSupportedException();
        public DateTimeOffset TimeStamp => throw new NotSupportedException();
        public TimeSpan Age => throw new NotSupportedException();
        public bool IsPrimitive => throw new NotSupportedException();
    }

    /// <summary>Builds one entry as a Beckhoff-shaped <see cref="IStructValue"/> — see <see cref="BeckhoffStructValue"/>.</summary>
    private static BeckhoffStructValue AsBeckhoffStructValue(
        string sKey = "BMK1Err404", string id = "BMK1", uint errorCode = 404,
        int errorType = 3, bool isActive = true, bool needsAck = true, bool isAcked = false,
        ushort year = 2026, ushort month = 6, ushort day = 17)
    {
        var time = new BeckhoffStructValue();
        time.Set("wYear", year); time.Set("wMonth", month); time.Set("wDayOfWeek", (ushort)3); time.Set("wDay", day);
        time.Set("wHour", (ushort)12); time.Set("wMinute", (ushort)0); time.Set("wSecond", (ushort)0);
        time.Set("wMilliseconds", (ushort)0);

        var entry = new BeckhoffStructValue();
        entry.Set("sKey", sKey); entry.Set("Id", id); entry.Set("ErrorCode", errorCode); entry.Set("ErrorType", errorType);
        entry.Set("IsActive", isActive); entry.Set("NeedsAck", needsAck); entry.Set("IsAcked", isAcked);
        entry.Set("PLCTimeStamp", time);
        return entry;
    }

    /// <summary>
    /// A fake standing in for Beckhoff's own array value (<c>DynamicArrayValue</c>),
    /// which is NOT <see cref="IEnumerable"/> itself — real ADS array notifications
    /// reach PlcAlarmBinder only through <see cref="IArrayValue.TryGetArrayElementValues"/>.
    /// </summary>
    private sealed class BeckhoffArrayValue(IEnumerable<object> elements) : IArrayValue
    {
        public bool TryGetArrayElementValues(out IEnumerable<object> elementValues)
        {
            elementValues = elements;
            return true;
        }

        public bool TrySetIndexValue(object[] indexes, object value) => throw new NotSupportedException();

        public bool TryGetIndexValue(int[] indices, out object value) => throw new NotSupportedException();

        // IValue members PlcAlarmBinder never touches for an array-level node.
        public ISymbol Symbol => throw new NotSupportedException();
        public IDataType DataType => throw new NotSupportedException();
        public ReadOnlyMemory<byte> CachedRaw => throw new NotSupportedException();
        public DateTimeOffset TimeStamp => throw new NotSupportedException();
        public TimeSpan Age => throw new NotSupportedException();
        public bool IsPrimitive => throw new NotSupportedException();
    }

    /// <summary>Wraps entries as a Beckhoff-shaped <see cref="IArrayValue"/> — see <see cref="BeckhoffArrayValue"/>.</summary>
    private static object AsBeckhoffArray(params object[] entries) => new BeckhoffArrayValue(entries);

    /// <summary>
    /// A minimal <see cref="ILogger"/> that records every formatted message, so a test
    /// can prove not just that a warning fired but how many times — the point of the
    /// unknown-severity logging tests below, where "logged once" is the whole claim.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Yields all four shapes <see cref="PlcAlarmBinder"/> may see: the dictionary
    /// tree, <see cref="ExpandoObject"/> (routed through the dictionary branch — see
    /// the class remarks), <see cref="TrueDynamicEntry"/> (routed through the DLR
    /// dynamic-binding branch), and the Beckhoff-shaped <see cref="IArrayValue"/> /
    /// <see cref="IStructValue"/> pair (routed through their own dedicated branches —
    /// the one every real ADS notification actually takes). Kept the historical
    /// "Both" name since it is already the <c>[MemberData]</c> reference below.
    /// </summary>
    public static TheoryData<object> BothShapes() => new()
    {
        new object?[] { AsDictionary() },
        new object?[] { AsDynamic() },
        new object?[] { AsTrueDynamic() },
        // NOT wrapped in an outer object?[] like the three rows above: those three
        // builders each return a single ENTRY, which needs a plain-array wrapper to
        // become "one whole alarm array" for Bind. AsBeckhoffArray already IS the
        // whole array (an IArrayValue), so wrapping it again here would hide it inside
        // a plain object?[] and route it through the WRONG branch — the eventual
        // Bind(array, ...) call must see the IArrayValue directly as notificationValue.
        AsBeckhoffArray(AsBeckhoffStructValue()),
    };

    [Theory]
    [MemberData(nameof(BothShapes))]
    public void Bind_MapsEveryMember(object array)
    {
        var alarms = PlcAlarmBinder.Bind(array, PlcId, Path, PlcClockKind.Unspecified, null);

        var alarm = Assert.Single(alarms);
        Assert.Equal("BMK1Err404", alarm.Key);
        Assert.Equal("BMK1", alarm.EquipmentId);
        Assert.Equal(404U, alarm.ErrorCode);
        Assert.Equal(AlarmSeverity.Error, alarm.Severity);
        Assert.True(alarm.IsActive);
        Assert.True(alarm.NeedsAcknowledgement);
        Assert.False(alarm.IsAcknowledged);
        Assert.Equal(new DateTime(2026, 6, 17, 12, 0, 0), alarm.PlcTimestamp);
        Assert.Equal(DateTimeKind.Unspecified, alarm.PlcTimestamp.Kind);
        Assert.Equal(0, alarm.SlotIndex);
        Assert.Equal(PlcId, alarm.PlcId);
    }

    [Fact]
    public void Bind_AssignsSlotIndexByPosition()
    {
        object?[] array = [AsDictionary(sKey: "A"), AsDictionary(sKey: "B"), AsDictionary(sKey: "C")];

        var alarms = PlcAlarmBinder.Bind(array, PlcId, Path, PlcClockKind.Unspecified, null);

        Assert.Equal([0, 1, 2], alarms.Select(a => a.SlotIndex));
        Assert.Equal(["A", "B", "C"], alarms.Select(a => a.Key));
    }

    [Fact]
    public void Bind_AppliesTheDeclaredClockKind()
    {
        object?[] array = [AsDictionary()];

        var utc = PlcAlarmBinder.Bind(array, PlcId, Path, PlcClockKind.Utc, null);
        var local = PlcAlarmBinder.Bind(array, PlcId, Path, PlcClockKind.Local, null);

        Assert.Equal(DateTimeKind.Utc, utc[0].PlcTimestamp.Kind);
        Assert.Equal(DateTimeKind.Local, local[0].PlcTimestamp.Kind);
    }

    [Fact]
    public void Bind_ZeroedTimestamp_YieldsDefault()
    {
        // An uninitialised PLC struct zeroes the date components. That is a legal
        // empty value, not a broken contract, so it must not throw.
        object?[] array = [AsDictionary(year: 0, month: 0, day: 0)];

        var alarms = PlcAlarmBinder.Bind(array, PlcId, Path, PlcClockKind.Unspecified, null);

        Assert.Equal(default, alarms[0].PlcTimestamp);
    }

    [Fact]
    public void Bind_UnknownSeverity_IsPreservedNotDropped()
    {
        object?[] array = [AsDictionary(errorType: 99)];

        var alarms = PlcAlarmBinder.Bind(array, PlcId, Path, PlcClockKind.Unspecified, null);

        Assert.Equal(99, (int)alarms[0].Severity);
    }

    [Fact]
    public void Bind_UnknownSeverity_LogsOnlyOnceAcrossNotifications()
    {
        // PlcAlarm.Severity's own doc promises "logged once". A stuck unknown severity
        // is read every notification at PLC cycle rate, so without a latch this would
        // flood the log forever. Uses a raw value no other test in this class uses
        // (424242), since the latch is process-wide static state shared across tests.
        var logger = new CapturingLogger();
        object?[] array = [AsDictionary(errorType: 424242)];

        PlcAlarmBinder.Bind(array, PlcId, Path, PlcClockKind.Unspecified, logger);
        PlcAlarmBinder.Bind(array, PlcId, Path, PlcClockKind.Unspecified, logger);

        var warning = Assert.Single(logger.Messages);
        Assert.Contains("424242", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_MissingMember_Throws_NamingTheMemberAndPath()
    {
        var entry = AsDictionary();
        entry.Remove("IsAcked");
        object?[] array = [entry];

        var ex = Assert.Throws<PlcAlarmShapeException>(
            () => PlcAlarmBinder.Bind(array, PlcId, Path, PlcClockKind.Unspecified, null));

        Assert.Contains("IsAcked", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_MissingMember_Throws_NamingTheMemberAndPath_ForTrueDynamicObject()
    {
        // ExpandoObject is caught by the dictionary branch (see the class remarks), so
        // the above test never actually proves the "members present" diagnostic works
        // on the branch that produces it for real hardware: GetDynamicMemberNames() is
        // the ONLY source of that list here, unlike the dictionary branch's Keys. This
        // proves the list reflects real member names rather than being empty or garbage.
        var entry = AsTrueDynamic();
        entry.Remove("IsAcked");
        object?[] array = [entry];

        var ex = Assert.Throws<PlcAlarmShapeException>(
            () => PlcAlarmBinder.Bind(array, PlcId, Path, PlcClockKind.Unspecified, null));

        Assert.Contains("IsAcked", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Path, ex.Message, StringComparison.Ordinal);
        Assert.Contains("sKey", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ErrorCode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_TrueDynamicObject_CaseInsensitiveMemberName_Resolves()
    {
        // Member lookup is case-insensitive everywhere in this package; the dictionary
        // branch already honours that explicitly. A dynamic object that disagreed
        // would be a latent bug, not a stylistic wrinkle — differently-cased names on
        // both the entry itself and the nested TIMESTRUCT must still resolve.
        var time = new TrueDynamicEntry();
        time.Set("WYEAR", (ushort)2026);
        time.Set("wMonth", (ushort)6);
        time.Set("wDay", (ushort)17);
        time.Set("wHour", (ushort)12);
        time.Set("wMinute", (ushort)0);
        time.Set("wSecond", (ushort)0);
        time.Set("wMilliseconds", (ushort)0);

        var entry = new TrueDynamicEntry();
        entry.Set("SKEY", "BMK1Err404");
        entry.Set("Id", "BMK1");
        entry.Set("ErrorCode", 404U);
        entry.Set("ErrorType", 3);
        entry.Set("IsActive", true);
        entry.Set("NeedsAck", true);
        entry.Set("IsAcked", false);
        entry.Set("PLCTimeStamp", time);
        object?[] array = [entry];

        var alarms = PlcAlarmBinder.Bind(array, PlcId, Path, PlcClockKind.Unspecified, null);

        var alarm = Assert.Single(alarms);
        Assert.Equal("BMK1Err404", alarm.Key);
        Assert.Equal(new DateTime(2026, 6, 17, 12, 0, 0), alarm.PlcTimestamp);
    }

    [Fact]
    public void Bind_MissingMember_Throws_NamingTheMemberAndPath_ForBeckhoffStructValue()
    {
        // Proves the "members present" diagnostic on the IStructValue branch too — this
        // is the branch a real ADS notification actually takes, and its member list
        // comes from GetDynamicMemberNames() rather than a dictionary's Keys, exactly
        // as for TrueDynamicEntry above.
        var entry = AsBeckhoffStructValue();
        entry.Remove("IsAcked");

        var ex = Assert.Throws<PlcAlarmShapeException>(
            () => PlcAlarmBinder.Bind(AsBeckhoffArray(entry), PlcId, Path, PlcClockKind.Unspecified, null));

        Assert.Contains("IsAcked", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Path, ex.Message, StringComparison.Ordinal);
        Assert.Contains("sKey", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ErrorCode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_BeckhoffStructValue_CaseInsensitiveMemberName_Resolves()
    {
        // IStructValue.TryGetMemberValue is exact-match only; PlcAlarmBinder must retry
        // case-insensitively here exactly as it does for the DLR dynamic fallback.
        var time = new BeckhoffStructValue();
        time.Set("WYEAR", (ushort)2026);
        time.Set("wMonth", (ushort)6);
        time.Set("wDay", (ushort)17);
        time.Set("wHour", (ushort)12);
        time.Set("wMinute", (ushort)0);
        time.Set("wSecond", (ushort)0);
        time.Set("wMilliseconds", (ushort)0);

        var entry = new BeckhoffStructValue();
        entry.Set("SKEY", "BMK1Err404");
        entry.Set("Id", "BMK1");
        entry.Set("ErrorCode", 404U);
        entry.Set("ErrorType", 3);
        entry.Set("IsActive", true);
        entry.Set("NeedsAck", true);
        entry.Set("IsAcked", false);
        entry.Set("PLCTimeStamp", time);

        var alarms = PlcAlarmBinder.Bind(AsBeckhoffArray(entry), PlcId, Path, PlcClockKind.Unspecified, null);

        var alarm = Assert.Single(alarms);
        Assert.Equal("BMK1Err404", alarm.Key);
        Assert.Equal(new DateTime(2026, 6, 17, 12, 0, 0), alarm.PlcTimestamp);
    }

    [Fact]
    public void Bind_WrongMemberType_Throws()
    {
        var entry = AsDictionary();
        entry["ErrorCode"] = "not a number";
        object?[] array = [entry];

        var ex = Assert.Throws<PlcAlarmShapeException>(
            () => PlcAlarmBinder.Bind(array, PlcId, Path, PlcClockKind.Unspecified, null));

        Assert.Contains("ErrorCode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_NumericKey_Throws()
    {
        // Convert.ChangeType alone would happily format 404 as the string "404" and
        // bind Key = "404" with no error — silently re-keying the whole outstanding
        // alarm set, since Key is the alarm's identity in the store. A retyped sKey
        // must fail loudly instead.
        var entry = AsDictionary();
        entry["sKey"] = 404;
        object?[] array = [entry];

        var ex = Assert.Throws<PlcAlarmShapeException>(
            () => PlcAlarmBinder.Bind(array, PlcId, Path, PlcClockKind.Unspecified, null));

        Assert.Contains("sKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_NumericIsActive_Throws()
    {
        // Convert.ChangeType alone converts any IConvertible number to bool (0 = false,
        // anything else = true) with no error — masking a retyped IsActive exactly as
        // silently as the numeric sKey case above.
        var entry = AsDictionary();
        entry["IsActive"] = 1;
        object?[] array = [entry];

        var ex = Assert.Throws<PlcAlarmShapeException>(
            () => PlcAlarmBinder.Bind(array, PlcId, Path, PlcClockKind.Unspecified, null));

        Assert.Contains("IsActive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_NonArrayValue_Throws()
    {
        var ex = Assert.Throws<PlcAlarmShapeException>(
            () => PlcAlarmBinder.Bind(42, PlcId, Path, PlcClockKind.Unspecified, null));

        Assert.Contains(Path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_SingleDictionaryEntryInsteadOfArray_Throws()
    {
        // A Dictionary IS an IEnumerable<KeyValuePair<...>>, so pointing SymbolPath at a
        // single alarm entry instead of the array must still produce the clear
        // "expected an array" message — not silently enumerate the entry's own
        // KeyValuePairs and report "Members present: Key, Value".
        var ex = Assert.Throws<PlcAlarmShapeException>(
            () => PlcAlarmBinder.Bind(AsDictionary(), PlcId, Path, PlcClockKind.Unspecified, null));

        Assert.Contains(Path, ex.Message, StringComparison.Ordinal);
        Assert.Contains("array of alarm entries", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Key, Value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_Null_YieldsEmpty()
    {
        var alarms = PlcAlarmBinder.Bind(null, PlcId, Path, PlcClockKind.Unspecified, null);

        Assert.Empty(alarms);
    }
}
