namespace Dahlke.TwinCAT.Ads.Alarms.Tests;

/// <summary>
/// Unit tests for <see cref="PlcAlarmStore"/>. Pure — no ADS, no hosting.
/// The store is driven with hand-built snapshots exactly as the binder would
/// produce them.
/// </summary>
public class PlcAlarmStoreTests
{
    private const string PlcId = "plc1";

    private static PlcAlarm Alarm(
        string key,
        string equipmentId = "BMK1",
        uint errorCode = 404,
        AlarmSeverity severity = AlarmSeverity.Error,
        bool isActive = true,
        bool needsAck = true,
        bool isAcked = false,
        int slot = 0,
        DateTime? timestamp = null) =>
        new()
        {
            Key = key,
            EquipmentId = equipmentId,
            ErrorCode = errorCode,
            Severity = severity,
            IsActive = isActive,
            NeedsAcknowledgement = needsAck,
            IsAcknowledged = isAcked,
            PlcTimestamp = timestamp ?? new DateTime(2026, 6, 17, 12, 0, 0),
            SlotIndex = slot,
            PlcId = PlcId,
        };

    [Fact]
    public void ActiveAlarm_IsOutstanding_AndRaises()
    {
        var store = new PlcAlarmStore(PlcId);

        var transitions = store.Apply([Alarm("BMK1Err404")]);

        var transition = Assert.Single(transitions);
        Assert.Equal(AlarmTransitionKind.Raised, transition.Kind);
        Assert.Equal("BMK1Err404", transition.Alarm.Key);
        Assert.Null(transition.Previous);
        Assert.Single(store.Outstanding);
    }

    [Fact]
    public void InactiveUnacknowledgedAlarm_StaysOutstanding()
    {
        // The ISA-18.2 "returned to normal, unacknowledged" state: the fault is
        // gone but nobody has seen it yet, so the alarm must survive.
        var store = new PlcAlarmStore(PlcId);
        store.Apply([Alarm("BMK1Err404")]);

        store.Apply([Alarm("BMK1Err404", isActive: false)]);

        Assert.Single(store.Outstanding);
    }

    [Fact]
    public void InactiveAcknowledgedAlarm_Ends()
    {
        var store = new PlcAlarmStore(PlcId);
        store.Apply([Alarm("BMK1Err404")]);

        var transitions = store.Apply([Alarm("BMK1Err404", isActive: false, isAcked: true)]);

        Assert.Contains(transitions, t => t.Kind == AlarmTransitionKind.Ended);
        Assert.Empty(store.Outstanding);
    }

    [Fact]
    public void InactiveAlarmThatNeedsNoAck_EndsImmediately()
    {
        var store = new PlcAlarmStore(PlcId);
        store.Apply([Alarm("BMK1Err404", needsAck: false)]);

        var transitions = store.Apply([Alarm("BMK1Err404", needsAck: false, isActive: false)]);

        Assert.Contains(transitions, t => t.Kind == AlarmTransitionKind.Ended);
        Assert.Empty(store.Outstanding);
    }

    [Fact]
    public void EmptyKeySlots_AreIgnored()
    {
        // A fixed PLC array is mostly empty slots; sKey is blank in every one.
        var store = new PlcAlarmStore(PlcId);

        var transitions = store.Apply([Alarm(""), Alarm("   "), Alarm("BMK1Err404", slot: 2)]);

        Assert.Single(transitions);
        Assert.Single(store.Outstanding);
    }

    [Fact]
    public void TwoAlarmsOnTheSameEquipment_StayDistinct()
    {
        // REGRESSION: the original branch keyed its cache on Id (the BMK), so a
        // second alarm on the same equipment overwrote the first, was never
        // reported, and clearing one reported the other as solved.
        var store = new PlcAlarmStore(PlcId);

        var transitions = store.Apply([
            Alarm("BMK1Err404", equipmentId: "BMK1", errorCode: 404, slot: 0),
            Alarm("BMK1Err500", equipmentId: "BMK1", errorCode: 500, slot: 1),
        ]);

        Assert.Equal(2, transitions.Count);
        Assert.All(transitions, t => Assert.Equal(AlarmTransitionKind.Raised, t.Kind));
        Assert.Equal(2, store.Outstanding.Count);
    }
}
