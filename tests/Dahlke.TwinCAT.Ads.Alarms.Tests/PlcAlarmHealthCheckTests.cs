using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dahlke.TwinCAT.Ads.Alarms.Tests;

/// <summary>Unit tests for <see cref="PlcAlarmHealthCheck"/>.</summary>
public class PlcAlarmHealthCheckTests
{
    private sealed class StubMonitor(params PlcAlarm[] outstanding) : IPlcAlarmMonitor
    {
        public IReadOnlyCollection<PlcAlarm> GetOutstanding() => outstanding;

        public IReadOnlyCollection<PlcAlarm> GetOutstanding(string plcId) =>
            [.. outstanding.Where(a => a.PlcId == plcId)];

        public event EventHandler<AlarmTransition>? AlarmChanged
        {
            add { } remove { }
        }

        public IObservable<AlarmTransition> Transitions =>
            System.Reactive.Linq.Observable.Never<AlarmTransition>();

        public Task<bool> AcknowledgeAsync(string plcId, string alarmKey, CancellationToken ct) =>
            Task.FromResult(false);
    }

    private static PlcAlarm Alarm(AlarmSeverity severity, string key = "BMK1Err404") => new()
    {
        Key = key,
        EquipmentId = "BMK1",
        ErrorCode = 404,
        Severity = severity,
        IsActive = true,
        NeedsAcknowledgement = true,
        IsAcknowledged = false,
        PlcTimestamp = new DateTime(2026, 6, 17, 12, 0, 0),
        SlotIndex = 0,
        PlcId = "plc1",
    };

    private static Task<HealthCheckResult> CheckAsync(params PlcAlarm[] outstanding) =>
        new PlcAlarmHealthCheck(new StubMonitor(outstanding), AlarmSeverity.Warning, AlarmSeverity.Error)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    [Fact]
    public async Task NoAlarms_IsHealthy()
    {
        var result = await CheckAsync();

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task InfoAlarm_IsHealthy()
    {
        var result = await CheckAsync(Alarm(AlarmSeverity.Info));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task WarningAlarm_IsDegraded()
    {
        var result = await CheckAsync(Alarm(AlarmSeverity.Warning));

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task ErrorAlarm_IsUnhealthy()
    {
        var result = await CheckAsync(Alarm(AlarmSeverity.Error));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task WorstSeverityWins()
    {
        var result = await CheckAsync(
            Alarm(AlarmSeverity.Info, "a"),
            Alarm(AlarmSeverity.Error, "b"),
            Alarm(AlarmSeverity.Warning, "c"));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task OutstandingAlarms_AreReportedInData()
    {
        var result = await CheckAsync(Alarm(AlarmSeverity.Error));

        Assert.True(result.Data.ContainsKey("plc1"));
    }
}
