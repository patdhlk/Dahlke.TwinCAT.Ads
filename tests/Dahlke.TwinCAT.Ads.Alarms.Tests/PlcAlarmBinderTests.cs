using System.Dynamic;

namespace Dahlke.TwinCAT.Ads.Alarms.Tests;

/// <summary>
/// Unit tests for <see cref="PlcAlarmBinder"/>.
/// </summary>
/// <remarks>
/// Every behavioural test runs against every shape the binder will really meet: the
/// dictionary tree the simulated connection carries, an <see cref="ExpandoObject"/>,
/// and a true dynamic object with no <see cref="System.Collections.IDictionary"/> in
/// sight. The middle one is a trap worth naming: <see cref="ExpandoObject"/>
/// implements <see cref="IDictionary{TKey, TValue}"/> under the hood, and nullable
/// annotations erase at runtime, so it is caught by PlcAlarmBinder's dictionary
/// branch, not its DLR dynamic-binding branch. Only <see cref="TrueDynamicEntry"/>
/// forces a read through that branch — which is the one every real ADS notification
/// actually takes. The original branch tested only a shape its own tests invented,
/// which is why it could be green while wrong; testing <see cref="ExpandoObject"/>
/// alone here would have repeated that mistake with better manners.
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
    /// Yields all three shapes <see cref="PlcAlarmBinder"/> may see: the dictionary
    /// tree, <see cref="ExpandoObject"/> (routed through the dictionary branch — see
    /// the class remarks), and <see cref="TrueDynamicEntry"/> (routed through the
    /// DLR dynamic-binding branch). Kept the historical "Both" name since it is
    /// already the <c>[MemberData]</c> reference below.
    /// </summary>
    public static TheoryData<object> BothShapes() => new()
    {
        new object?[] { AsDictionary() },
        new object?[] { AsDynamic() },
        new object?[] { AsTrueDynamic() },
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
    public void Bind_NonArrayValue_Throws()
    {
        var ex = Assert.Throws<PlcAlarmShapeException>(
            () => PlcAlarmBinder.Bind(42, PlcId, Path, PlcClockKind.Unspecified, null));

        Assert.Contains(Path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_Null_YieldsEmpty()
    {
        var alarms = PlcAlarmBinder.Bind(null, PlcId, Path, PlcClockKind.Unspecified, null);

        Assert.Empty(alarms);
    }
}
