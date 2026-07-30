using System.Dynamic;

namespace Dahlke.TwinCAT.Ads.Alarms.Tests;

/// <summary>
/// Unit tests for <see cref="PlcAlarmBinder"/>.
/// </summary>
/// <remarks>
/// Every behavioural test runs against BOTH shapes the binder will really meet:
/// the dictionary tree the simulated connection carries, and the dynamic object a
/// real ADS notification payload decodes to. The original branch tested only a
/// shape its own tests invented, which is why it could be green while wrong.
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

    public static TheoryData<object> BothShapes() => new()
    {
        new object?[] { AsDictionary() },
        new object?[] { AsDynamic() },
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
