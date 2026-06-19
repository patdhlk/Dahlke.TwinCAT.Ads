using System.Dynamic;
using System.Globalization;
using FluentAssertions;
using ErrorHandler.Service;

namespace Test.ErrorHandler.Service;

/// <summary>
/// Contains unit tests for the <see cref="MessageDictionary"/> class,
/// verifying state tracking, change logs, and edge-case handling for system alarms.
/// </summary>
public class MessageDictionaryTests
{
    private readonly MessageDictionary _sut = new();

    [Fact]
    public void UpdateAndGetChanges_ShouldReturnEmpty_WhenCurrentArrayIsNull()
    {
        // Arrange - Pass a null reference to the method
        dynamic? input = null;

        // Act - Process the null input
        List<string> result = _sut.UpdateAndGetChanges(input);

        // Assert - Expect an empty log list without any exceptions thrown
        result.Should().BeEmpty();
    }

    [Fact]
    public void UpdateAndGetChanges_ShouldLogNewAlarm_WhenAlarmIsAddedForTheFirstTime()
    {
        // Arrange - Setup a brand new active alarm
        var mockAlarm = CreateMockAlarm(id: "ERR_001", errorCode: 404U, isActive: true, isAcked: false);
        var activeAlarms = new[] { mockAlarm };

        // Act - Process the new alarm
        var logs = _sut.UpdateAndGetChanges(activeAlarms);

        // Assert - Verify that a registration/creation entry is logged with correct details
        logs.Should().ContainSingle().Which.Should().MatchRegex(".*\\[NEW ERROR\\].*'ERR_001'.*ErrorCode: 404.*");
    }

    [Fact]
    public void UpdateAndGetChanges_ShouldLogAcknowledgment_WhenIsAckedChangesToTrue()
    {
        // Arrange - Setup the same alarm transitioning from unacknowledged to acknowledged
        var initialAlarm = CreateMockAlarm(id: "ERR_001", errorCode: 404U, isActive: true, isAcked: false);
        var secondaryAlarm = CreateMockAlarm(id: "ERR_001", errorCode: 404U, isActive: true, isAcked: true);

        // Act - First scan caches the alarm; second scan processes the acknowledgment change
        _sut.UpdateAndGetChanges(new[] { initialAlarm });
        var logs = _sut.UpdateAndGetChanges(new[] { secondaryAlarm });

        // Assert - Verify the acknowledgement event was caught and logged explicitly
        logs.Should().ContainSingle().Which.Should().Be("[ACKNOWLEDGED] Error 'ERR_001' has been acknowledged");
    }

    [Fact]
    public void UpdateAndGetChanges_ShouldLogSolved_WhenAlarmDisappearsFromInputArray()
    {
        // Arrange - Setup an active alarm for the first scan, and an empty array for the second
        var mockAlarm = CreateMockAlarm(id: "ERR_001", errorCode: 404U, isActive: true, isAcked: false);
        var initialScan = new[] { mockAlarm };
        var secondaryScan = Array.Empty<object>();

        // Act - First scan introduces the alarm; second scan simulates the alarm clearing out
        _sut.UpdateAndGetChanges(initialScan);
        var logs = _sut.UpdateAndGetChanges(secondaryScan);

        // Assert - Verify that dropping the alarm from the stream triggers a resolution log
        logs.Should().ContainSingle().Which.Should().Be("[SOLVED] 'ERR_001' has been resolved");
    }

    [Fact]
    public void UpdateAndGetChanges_ShouldHandleInvalidDateGracefully_ByUsingDateTimeMinValue()
    {
        // Arrange - Setup an alarm passing zeroed-out structures mimicking a bad PLC clock cycle
        var mockAlarm = CreateMockAlarm(id: "ERR_BAD_DATE", errorCode: 100U, isActive: true, isAcked: false, useInvalidDate: true);
        var activeAlarms = new[] { mockAlarm };

        // Act - Process the malformed timestamp
        var logs = _sut.UpdateAndGetChanges(activeAlarms);

        // Assert - Verify system gracefully falls back to DateTime.MinValue instead of crashing
        logs.Should().ContainSingle().Which.Should().Contain(DateTime.MinValue.ToString(CultureInfo.CurrentCulture));
    }

    #region Helpers

    /// <summary>
    /// Factory method to generate highly-dynamic alarm data payloads mimicking PLC structures.
    /// </summary>
    /// <param name="id">The unique functional identifier of the alarm (e.g., ERR_001).</param>
    /// <param name="errorCode">The underlying hardware/software error code status.</param>
    /// <param name="isActive">Determines if the alarm state is currently raised.</param>
    /// <param name="isAcked">Determines if an operator has acknowledged the alarm state.</param>
    /// <param name="useInvalidDate">If true, zeroes out the timestamp structure to simulate corruption or uninitialized states.</param>
    /// <returns>A dynamic <see cref="ExpandoObject"/> mirroring the structural expectations of the MessageDictionary processing routine.</returns>
    private static dynamic CreateMockAlarm(string id, uint errorCode, bool isActive, bool isAcked, bool useInvalidDate = false)
    {
        dynamic plcTime = new ExpandoObject();
        plcTime.wYear = useInvalidDate ? (ushort)0 : (ushort)2026;
        plcTime.wMonth = useInvalidDate ? (ushort)0 : (ushort)6;
        plcTime.wDay = useInvalidDate ? (ushort)0 : (ushort)17;
        plcTime.wHour = useInvalidDate ? (ushort)0 : (ushort)12;
        plcTime.wMinute = (ushort)0;
        plcTime.wSecond = (ushort)0;
        plcTime.wMilliseconds = (ushort)0;

        dynamic mockAlarm = new ExpandoObject();
        mockAlarm.Id = id;
        mockAlarm.ErrorType = 3;
        mockAlarm.ErrorCode = errorCode;
        mockAlarm.IsActive = isActive;
        mockAlarm.IsAcked = isAcked;
        mockAlarm.NeedsAck = true;
        mockAlarm.PLCTimeStamp = plcTime;

        return mockAlarm;
    }

    #endregion
}
