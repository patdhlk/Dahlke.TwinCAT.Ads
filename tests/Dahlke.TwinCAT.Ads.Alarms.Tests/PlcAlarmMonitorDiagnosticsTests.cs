using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dahlke.TwinCAT.Ads.Alarms.Tests;

/// <summary>
/// Tests how often <see cref="PlcAlarmMonitor"/> reports a notification it could not handle.
/// </summary>
/// <remarks>
/// <para>
/// A shape mismatch is a property of the PLC's TYPE, not of one notification, so once it starts
/// it does not stop. Reported per notification at the default <c>CycleTimeMs = 200</c> that is
/// five stack traces per second per target, indefinitely — log exhaustion during exactly the
/// incident an operator would be trying to diagnose, and every line after the first is identical.
/// So each failure is reported once and then latched until a notification binds again.
/// </para>
/// <para>
/// Simulated callbacks run synchronously on the writing thread, so once a write returns its
/// notification has been handled and the log can be asserted on without waiting.
/// </para>
/// </remarks>
public class PlcAlarmMonitorDiagnosticsTests
{
    private const string PlcId = "plc1";
    private const string Path = "GVL.Errors";

    /// <summary>The distinctive part of the shape-mismatch diagnostic.</summary>
    private const string ShapeMessage = "does not match the shape";

    /// <summary>The distinctive part of the broad catch-all's diagnostic.</summary>
    private const string UnexpectedMessage = "Unexpected failure handling an alarm notification";

    /// <summary>The distinctive part of the line that says the latch has re-armed.</summary>
    private const string ResumedMessage = "binding notifications again";

    private static async Task<(IHost Host, CapturingLoggerProvider Log)> StartAsync(
        IPlcAlarmDialect dialect, params string[] plcIds)
    {
        var targets = plcIds.Length == 0 ? [PlcId] : plcIds;
        var builder = Host.CreateApplicationBuilder();
        var log = new CapturingLoggerProvider();

        builder.Services.AddTwinCatAdsSimulation(o =>
        {
            for (var i = 0; i < targets.Length; i++)
            {
                o.Targets[targets[i]] = new PlcTargetOptions
                {
                    AmsNetId = $"1.2.3.4.5.{i + 1}",
                    InitialValues = { [Path] = Array.Empty<object?>() },
                };
            }
        });

        builder.Services.Configure<PlcAlarmsOptions>(o =>
        {
            foreach (var plcId in targets)
                o.Targets[plcId] = new PlcAlarmTargetOptions { SymbolPath = Path, CycleTimeMs = 50 };
        });

        builder.Services.AddSingleton<ILoggerProvider>(log);
        builder.Services.AddSingleton(dialect);

        builder.Services.AddTwinCatAdsAlarms(builder.Configuration);

        var host = builder.Build();
        await host.StartAsync();

        return (host, log);
    }

    /// <summary>
    /// Fires one notification on <paramref name="plcId"/>. A NEW array instance every time: the
    /// simulated store compares <c>object?[]</c> by reference, so a repeated instance would not
    /// fire and a test asserting "logged once" would pass for the wrong reason.
    /// </summary>
    private static async Task NotifyAsync(IHost host, string plcId = PlcId)
    {
        var connection = host.Services.GetRequiredService<IAdsConnectionPool>().GetConnection(plcId);

        await connection.WriteValueAsync(Path, new object?[] { Alarm() }, CancellationToken.None);
    }

    private static Dictionary<string, object?> Alarm() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sKey"] = "BMK1Err404",
            ["Id"] = "BMK1",
            ["ErrorCode"] = 404u,
            ["ErrorType"] = 3,
            ["IsActive"] = true,
            ["NeedsAck"] = true,
            ["IsAcked"] = false,
            ["PLCTimeStamp"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["wYear"] = (ushort)2026, ["wMonth"] = (ushort)6, ["wDayOfWeek"] = (ushort)3,
                ["wDay"] = (ushort)17, ["wHour"] = (ushort)12, ["wMinute"] = (ushort)0,
                ["wSecond"] = (ushort)0, ["wMilliseconds"] = (ushort)0,
            },
        };

    private static int Count(CapturingLoggerProvider log, LogLevel level, string fragment) =>
        log.Entries.Count(e =>
            e.StartsWith($"{level}: ", StringComparison.Ordinal) &&
            e.Contains(fragment, StringComparison.Ordinal));

    [Fact]
    public async Task RepeatedShapeMismatch_IsReportedOnce_NotPerNotification()
    {
        var dialect = new SwitchableDialect { Failure = () => new PlcAlarmShapeException("bad shape") };
        var (host, log) = await StartAsync(dialect);
        using var _ = host;

        for (var i = 0; i < 5; i++)
            await NotifyAsync(host);

        Assert.Equal(1, Count(log, LogLevel.Error, ShapeMessage));

        await host.StopAsync();
    }

    [Fact]
    public async Task RepeatedUnexpectedFailure_IsReportedOnce_NotPerNotification()
    {
        // The broad catch below the shape one — a consumer's IAlarmTextCatalog.Resolve or a
        // dialect throwing something else. Same repeat rate, same flood.
        var dialect = new SwitchableDialect { Failure = () => new InvalidOperationException("boom") };
        var (host, log) = await StartAsync(dialect);
        using var _ = host;

        for (var i = 0; i < 5; i++)
            await NotifyAsync(host);

        Assert.Equal(1, Count(log, LogLevel.Error, UnexpectedMessage));

        await host.StopAsync();
    }

    [Fact]
    public async Task AFailureThatRecovers_ReportsAgainIfItRecurs()
    {
        // The distinction worth preserving: a genuinely transient malformation that recovers and
        // then comes back is news again. A latch that never re-armed would report the second
        // outage nowhere at all.
        var dialect = new SwitchableDialect { Failure = () => new PlcAlarmShapeException("bad shape") };
        var (host, log) = await StartAsync(dialect);
        using var _ = host;

        await NotifyAsync(host);
        await NotifyAsync(host);

        dialect.Failure = null;
        await NotifyAsync(host);

        dialect.Failure = () => new PlcAlarmShapeException("bad shape");
        await NotifyAsync(host);

        Assert.Equal(2, Count(log, LogLevel.Error, ShapeMessage));

        await host.StopAsync();
    }

    [Fact]
    public async Task ANotificationThatBindsAgain_ReportsThatMonitoringResumed()
    {
        // An operator who sees one Error and then silence cannot tell "it recovered" from "the
        // latch is holding". The all-clear says which, and carries the count of everything the
        // latch swallowed so the extent of the outage is still on the record.
        var dialect = new SwitchableDialect { Failure = () => new PlcAlarmShapeException("bad shape") };
        var (host, log) = await StartAsync(dialect);
        using var _ = host;

        for (var i = 0; i < 3; i++)
            await NotifyAsync(host);

        dialect.Failure = null;
        await NotifyAsync(host);

        var line = Assert.Single(
            log.Entries, e => e.Contains(ResumedMessage, StringComparison.Ordinal));

        Assert.StartsWith("Information: ", line, StringComparison.Ordinal);
        Assert.Contains("3", line, StringComparison.Ordinal);

        await host.StopAsync();
    }

    [Fact]
    public async Task ASuccessfulNotification_DoesNotAnnounceARecoveryThatNeverHappened()
    {
        // The clear runs on every good notification, which is five times a second per target.
        // If it logged unconditionally it would be the very flood this change removes.
        var (host, log) = await StartAsync(new SwitchableDialect());
        using var _ = host;

        for (var i = 0; i < 5; i++)
            await NotifyAsync(host);

        Assert.Equal(0, Count(log, LogLevel.Information, ResumedMessage));

        await host.StopAsync();
    }

    [Fact]
    public async Task TheLatchIsPerTarget()
    {
        // Keyed by (plcId, symbolPath), so one PLC with a changed type does not silence the
        // report for a second PLC that develops the same fault later.
        var dialect = new SwitchableDialect { Failure = () => new PlcAlarmShapeException("bad shape") };
        var (host, log) = await StartAsync(dialect, "plc1", "plc2");
        using var _ = host;

        await NotifyAsync(host, "plc1");
        await NotifyAsync(host, "plc1");
        await NotifyAsync(host, "plc2");

        Assert.Equal(2, Count(log, LogLevel.Error, ShapeMessage));
        Assert.Equal(1, Count(log, LogLevel.Error, "plc2"));

        await host.StopAsync();
    }

    [Fact]
    public async Task TheTwoDiagnostics_AreLatchedSeparately()
    {
        // A shape mismatch already latched must not swallow the first occurrence of the
        // catch-all below it. They have different causes and different fixes, so one being
        // known is no reason to hide the other.
        var dialect = new SwitchableDialect { Failure = () => new PlcAlarmShapeException("bad shape") };
        var (host, log) = await StartAsync(dialect);
        using var _ = host;

        await NotifyAsync(host);

        dialect.Failure = () => new InvalidOperationException("boom");
        await NotifyAsync(host);

        Assert.Equal(1, Count(log, LogLevel.Error, ShapeMessage));
        Assert.Equal(1, Count(log, LogLevel.Error, UnexpectedMessage));

        await host.StopAsync();
    }

    /// <summary>
    /// Binds exactly as the shipped dialect does, or throws whatever the test currently wants —
    /// the two states a target moves between when a malformation starts and later clears.
    /// </summary>
    private sealed class SwitchableDialect : IPlcAlarmDialect
    {
        private readonly ErrorHandlerAlarmDialect _binding = new();

        /// <summary>
        /// When set, <c>Bind</c> throws this instead of binding. A factory, so each notification
        /// throws a fresh exception with its own stack — as a real repeated fault would.
        /// </summary>
        public Func<Exception>? Failure { get; set; }

        public IReadOnlyList<PlcAlarm> Bind(AlarmBindContext context) =>
            Failure is { } failure ? throw failure() : _binding.Bind(context);

        public Task<bool> AcknowledgeAsync(AlarmAcknowledgeContext context, CancellationToken ct) =>
            Task.FromResult(true);
    }

    /// <summary>Captures every log entry, so a test can assert what an operator would find.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries
        {
            get { lock (_entries) { return [.. _entries]; } }
        }

        public ILogger CreateLogger(string categoryName) => new Entry(this);

        public void Dispose() { }

        private void Add(string message)
        {
            lock (_entries) { _entries.Add(message); }
        }

        private sealed class Entry(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }
}
