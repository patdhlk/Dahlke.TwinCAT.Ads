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

    [Theory]
    [InlineData(-1)]   // E_ErrorType is SIGNED; the binder preserves what it cannot name
    [InlineData(99)]   // and an unknown value above the named range is equally uninterpretable
    public async Task UnrecognisedSeverity_IsNeverHealthy(int raw)
    {
        // The ladder used to end in `else Healthy`, so a severity below None cleared neither
        // threshold and was announced as Healthy — with "worst severity -1" in its own
        // description. An outstanding alarm nothing can rank is not a healthy PLC.
        var result = await CheckAsync(Alarm((AlarmSeverity)raw));

        Assert.NotEqual(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task SeverityBelowNone_IsDegraded_NotUnhealthy()
    {
        // Degraded rather than Unhealthy is the deliberate choice: an alarm this package cannot
        // rank is a reason to look, not proof of a fault, and reporting Unhealthy would pull a
        // serving instance out of rotation over a PLC enum member we have not been taught.
        var result = await CheckAsync(Alarm((AlarmSeverity)(-1)));

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task SeverityAboveTheNamedRange_IsUnhealthy()
    {
        // The unhealthyAt comparison is tested BEFORE the recognised-severity guard, so a value
        // above Error still reports Unhealthy rather than being softened to Degraded by it.
        var result = await CheckAsync(Alarm((AlarmSeverity)99));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task OutstandingAlarms_AreReportedInData()
    {
        var result = await CheckAsync(Alarm(AlarmSeverity.Error));

        Assert.True(result.Data.ContainsKey("plc1"));
    }
}
