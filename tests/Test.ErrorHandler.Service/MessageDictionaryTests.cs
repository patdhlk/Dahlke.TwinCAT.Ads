using System.Dynamic;
using System.Globalization;
using FluentAssertions;
using ErrorHandler.Service;

namespace Test.ErrorHandler.Service;

public class MessageDictionaryTests
{
    private readonly MessageDictionary _sut = new();

    [Fact]
    public void UpdateAndGetChanges_ShouldReturnEmpty_WhenCurrentArrayIsNull()
    {
        // Arrange
        dynamic? input = null;

        // Act
        List<string> result = _sut.UpdateAndGetChanges(input);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void UpdateAndGetChanges_ShouldLogNewAlarm_WhenAlarmIsAddedForTheFirstTime()
    {
        // Arrange
        dynamic plcTime = new ExpandoObject();
        plcTime.wYear = (ushort)2026;
        plcTime.wMonth = (ushort)6;
        plcTime.wDay = (ushort)17;
        plcTime.wHour = (ushort)12;
        plcTime.wMinute = (ushort)0;
        plcTime.wSecond = (ushort)0;
        plcTime.wMilliseconds = (ushort)0;

        dynamic mockAlarm = new ExpandoObject();
        mockAlarm.Id = "ERR_001";
        mockAlarm.ErrorType = 3;
        mockAlarm.ErrorCode = 404U;
        mockAlarm.IsActive = true;
        mockAlarm.IsAcked = false;
        mockAlarm.NeedsAck = true;
        mockAlarm.PLCTimeStamp = plcTime;

        var activeAlarms = new[] { mockAlarm };

        // Act
        var logs = _sut.UpdateAndGetChanges(activeAlarms);

        // Assert
        logs.Should().ContainSingle();
        logs[0].Should().Contain("[NEW ERROR]");
        logs[0].Should().Contain("'ERR_001'");
        logs[0].Should().Contain("ErrorCode: 404");
    }

    [Fact]
    public void UpdateAndGetChanges_ShouldLogAcknowledgment_WhenIsAckedChangesToTrue()
    {
        // Arrange
        dynamic plcTime = new ExpandoObject();
        plcTime.wYear = (ushort)2026;
        plcTime.wMonth = (ushort)6;
        plcTime.wDay = (ushort)17;
        plcTime.wHour = (ushort)12;
        plcTime.wMinute = (ushort)0;
        plcTime.wSecond = (ushort)0;
        plcTime.wMilliseconds = (ushort)0;

        // Scan 1: Unacknowledged alarm
        dynamic initialAlarm = new ExpandoObject();
        initialAlarm.Id = "ERR_001";
        initialAlarm.ErrorType = 3;
        initialAlarm.ErrorCode = 404U;
        initialAlarm.IsActive = true;
        initialAlarm.IsAcked = false;
        initialAlarm.NeedsAck = true;
        initialAlarm.PLCTimeStamp = plcTime;

        // Scan 2: Acknowledged alarm
        dynamic secondaryAlarm = new ExpandoObject();
        secondaryAlarm.Id = "ERR_001";
        secondaryAlarm.ErrorType = 3;
        secondaryAlarm.ErrorCode = 404U;
        secondaryAlarm.IsActive = true;
        secondaryAlarm.IsAcked = true;
        secondaryAlarm.NeedsAck = true;
        secondaryAlarm.PLCTimeStamp = plcTime;

        // Act
        _sut.UpdateAndGetChanges(new[] { initialAlarm }); // Seed the cache
        var logs = _sut.UpdateAndGetChanges(new[] { secondaryAlarm });

        // Assert
        logs.Should().ContainSingle();
        logs[0].Should().Be("[ACKNOWLEDGED] Error 'ERR_001' has been acknowledged");
    }

    [Fact]
    public void UpdateAndGetChanges_ShouldLogSolved_WhenAlarmDisappearsFromInputArray()
    {
        // Arrange
        dynamic plcTime = new ExpandoObject();
        plcTime.wYear = (ushort)2026;
        plcTime.wMonth = (ushort)6;
        plcTime.wDay = (ushort)17;
        plcTime.wHour = (ushort)12;
        plcTime.wMinute = (ushort)0;
        plcTime.wSecond = (ushort)0;
        plcTime.wMilliseconds = (ushort)0;

        dynamic mockAlarm = new ExpandoObject();
        mockAlarm.Id = "ERR_001";
        mockAlarm.ErrorType = 3;
        mockAlarm.ErrorCode = 404U;
        mockAlarm.IsActive = true;
        mockAlarm.IsAcked = false;
        mockAlarm.NeedsAck = true;
        mockAlarm.PLCTimeStamp = plcTime;

        var initialScan = new[] { mockAlarm };
        var secondaryScan = Array.Empty<object>(); // Alarm clears out

        // Act
        _sut.UpdateAndGetChanges(initialScan); // Seed the cache
        var logs = _sut.UpdateAndGetChanges(secondaryScan);

        // Assert
        logs.Should().ContainSingle();
        logs[0].Should().Be("[SOLVED] 'ERR_001' has been resolved");
    }

    [Fact]
    public void UpdateAndGetChanges_ShouldHandleInvalidDateGracefully_ByUsingDateTimeMinValue()
    {
        // Arrange
        dynamic invalidPlcTime = new ExpandoObject();
        invalidPlcTime.wYear = (ushort)0;
        invalidPlcTime.wMonth = (ushort)0;
        invalidPlcTime.wDay = (ushort)0;
        invalidPlcTime.wHour = (ushort)0;
        invalidPlcTime.wMinute = (ushort)0;
        invalidPlcTime.wSecond = (ushort)0;
        invalidPlcTime.wMilliseconds = (ushort)0;

        dynamic mockAlarm = new ExpandoObject();
        mockAlarm.Id = "ERR_BAD_DATE";
        mockAlarm.ErrorType = 1;
        mockAlarm.ErrorCode = 100U;
        mockAlarm.IsActive = true;
        mockAlarm.IsAcked = false;
        mockAlarm.NeedsAck = true;
        mockAlarm.PLCTimeStamp = invalidPlcTime;

        var activeAlarms = new[] { mockAlarm };

        // Act
        var logs = _sut.UpdateAndGetChanges(activeAlarms);

        // Assert
        logs.Should().ContainSingle();
        logs[0].Should().Contain(DateTime.MinValue.ToString(CultureInfo.CurrentCulture));
    }
}
