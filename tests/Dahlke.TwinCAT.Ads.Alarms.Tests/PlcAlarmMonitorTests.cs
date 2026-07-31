using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dahlke.TwinCAT.Ads.Alarms.Tests;

/// <summary>
/// End-to-end tests for <see cref="PlcAlarmMonitor"/> over a SIMULATED connection —
/// DI, options binding, subscription, binder and store together.
/// </summary>
/// <remarks>
/// The simulated connection is a flat store of dotted paths, so the alarm array is
/// seeded code-first as an <c>object?[]</c> of dictionaries. Change detection falls
/// back to reference equality, so writing a fresh array instance fires the
/// subscription.
/// </remarks>
public class PlcAlarmMonitorTests
{
    private const string PlcId = "plc1";
    private const string Path = "GVL.Errors";

    private static Dictionary<string, object?> Entry(
        string sKey, bool isActive = true, bool isAcked = false, uint errorCode = 404) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sKey"] = sKey,
            ["Id"] = "BMK1",
            ["ErrorCode"] = errorCode,
            ["ErrorType"] = 3,
            ["IsActive"] = isActive,
            ["NeedsAck"] = true,
            ["IsAcked"] = isAcked,
            ["PLCTimeStamp"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["wYear"] = (ushort)2026, ["wMonth"] = (ushort)6, ["wDayOfWeek"] = (ushort)3,
                ["wDay"] = (ushort)17, ["wHour"] = (ushort)12, ["wMinute"] = (ushort)0,
                ["wSecond"] = (ushort)0, ["wMilliseconds"] = (ushort)0,
            },
        };

    /// <summary>
    /// Builds a host with one simulated target per entry, each alarm array seeded empty,
    /// starts it, and returns the running host plus its monitor.
    /// </summary>
    /// <param name="configure">
    /// Runs BEFORE <c>AddTwinCatAdsAlarms</c>, so a test can register its own
    /// <see cref="IPlcAlarmDialect"/> and have it win over the shipped default — which is
    /// exactly the override the interface's own documentation promises.
    /// </param>
    /// <param name="targets">One <c>(plcId, symbolPath)</c> pair per simulated target.</param>
    private static async Task<(IHost Host, IPlcAlarmMonitor Monitor)> StartTargetsAsync(
        Action<IServiceCollection>? configure,
        params (string PlcId, string SymbolPath)[] targets)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddTwinCatAdsSimulation(o =>
        {
            for (var i = 0; i < targets.Length; i++)
            {
                o.Targets[targets[i].PlcId] = new PlcTargetOptions
                {
                    // Distinct per target — two PLCs never share an AMS Net ID.
                    AmsNetId = $"1.2.3.4.5.{i + 1}",
                    InitialValues = { [targets[i].SymbolPath] = Array.Empty<object?>() },
                };
            }
        });

        builder.Services.Configure<PlcAlarmsOptions>(o =>
        {
            foreach (var (plcId, symbolPath) in targets)
                o.Targets[plcId] = new PlcAlarmTargetOptions { SymbolPath = symbolPath, CycleTimeMs = 50 };
        });

        configure?.Invoke(builder.Services);

        builder.Services.AddTwinCatAdsAlarms(builder.Configuration);

        var host = builder.Build();
        await host.StartAsync();

        return (host, host.Services.GetRequiredService<IPlcAlarmMonitor>());
    }

    /// <summary>The single-target host every test but the multi-target one runs against.</summary>
    private static Task<(IHost Host, IPlcAlarmMonitor Monitor)> StartAsync(
        Action<IServiceCollection>? configure = null) =>
        StartTargetsAsync(configure, (PlcId, Path));

    /// <summary>
    /// Puts <paramref name="entries"/> on the simulated PLC as the whole alarm array.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The array is written whole and nothing else is: the monitor reads alarms out of the
    /// notification value, and acknowledgement addresses an alarm by key through a PLC method
    /// call, so no test needs <c>GVL.Errors[0].sKey</c> to be a readable path of its own. This
    /// helper used to mirror every scalar member onto such a path, purely so the old
    /// acknowledge could read a slot's occupant back — that mechanism is gone.
    /// </para>
    /// <para>
    /// A NEW array instance every time: the simulated store's change comparer falls back to
    /// reference equality for <c>object?[]</c>, so a mutated array would not fire.
    /// </para>
    /// </remarks>
    private static async Task WriteArrayToAsync(
        IHost host, string plcId, string path, params object?[] entries)
    {
        var connection = host.Services.GetRequiredService<IAdsConnectionPool>().GetConnection(plcId);

        await connection.WriteValueAsync(path, entries, CancellationToken.None);
    }

    private static Task WriteArrayAsync(IHost host, params object?[] entries) =>
        WriteArrayToAsync(host, PlcId, Path, entries);

    /// <summary>Waits for a condition the notification thread will satisfy.</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);

        Assert.True(condition(), "Condition was not satisfied within the timeout.");
    }

    [Fact]
    public async Task RaisedAlarm_AppearsInOutstandingAndRaisesTheEvent()
    {
        var (host, monitor) = await StartAsync();
        using var _ = host;

        var transitions = new List<AlarmTransition>();
        monitor.AlarmChanged += (_, t) => { lock (transitions) { transitions.Add(t); } };

        await WriteArrayAsync(host, Entry("BMK1Err404"));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 1);

        lock (transitions)
        {
            Assert.Contains(transitions, t => t.Kind == AlarmTransitionKind.Raised);
        }

        await host.StopAsync();
    }

    [Fact]
    public async Task ClearedAndAcknowledgedAlarm_LeavesOutstanding()
    {
        var (host, monitor) = await StartAsync();
        using var _ = host;

        await WriteArrayAsync(host, Entry("BMK1Err404"));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 1);

        await WriteArrayAsync(host, Entry("BMK1Err404", isActive: false, isAcked: true));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 0);

        await host.StopAsync();
    }

    [Fact]
    public async Task GetOutstanding_FiltersByPlcId()
    {
        var (host, monitor) = await StartAsync();
        using var _ = host;

        await WriteArrayAsync(host, Entry("BMK1Err404"));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 1);

        Assert.Single(monitor.GetOutstanding(PlcId));
        Assert.Empty(monitor.GetOutstanding("someOtherPlc"));

        await host.StopAsync();
    }

    [Fact]
    public async Task TwoTargets_AreTrackedIndependently()
    {
        const string OtherPlcId = "plc2";
        const string OtherPath = "GVL.Faults";

        var (host, monitor) = await StartTargetsAsync(null, (PlcId, Path), (OtherPlcId, OtherPath));
        using var _ = host;

        await WriteArrayToAsync(host, PlcId, Path, Entry("BMK1Err404"));
        await WriteArrayToAsync(host, OtherPlcId, OtherPath, Entry("BMK2Err500", errorCode: 500));

        await WaitForAsync(() => monitor.GetOutstanding().Count == 2);

        var first = Assert.Single(monitor.GetOutstanding(PlcId));
        var second = Assert.Single(monitor.GetOutstanding(OtherPlcId));

        Assert.Equal("BMK1Err404", first.Key);
        Assert.Equal("BMK2Err500", second.Key);

        // Each alarm reports the target it was actually read from — not whichever
        // subscription fired last. A loop variable captured once for all targets, rather
        // than per iteration, routes every notification into one store and fails here.
        Assert.Equal(PlcId, first.PlcId);
        Assert.Equal(OtherPlcId, second.PlcId);

        await host.StopAsync();
    }

    [Fact]
    public async Task MalformedSnapshot_IsDroppedAndTheSubscriptionRecovers()
    {
        var (host, monitor) = await StartAsync();
        using var _ = host;

        await WriteArrayAsync(host, Entry("BMK1Err404"));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 1);

        // An entry missing sKey: the binder throws PlcAlarmShapeException, which the
        // monitor logs at Error and drops. Simulated callbacks run synchronously on the
        // writing thread, so once the write returns the bad snapshot has been handled —
        // there is nothing left to wait for.
        var malformed = Entry("BMK1Err404");
        malformed.Remove("sKey");
        await WriteArrayAsync(host, malformed);

        // Dropped whole: the outstanding set still shows the last GOOD reading rather
        // than a partially-bound one.
        Assert.Equal("BMK1Err404", Assert.Single(monitor.GetOutstanding()).Key);

        // The half that matters — a subscription that died on the bad notification would
        // still satisfy every assertion above.
        await WriteArrayAsync(host, Entry("BMK1Err404"), Entry("BMK2Err500", errorCode: 500));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 2);

        await host.StopAsync();
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotStopDelivery()
    {
        var (host, monitor) = await StartAsync();
        using var _ = host;

        var delivered = 0;
        monitor.AlarmChanged += (_, _) => throw new InvalidOperationException("boom");
        monitor.AlarmChanged += (_, _) => Interlocked.Increment(ref delivered);

        await WriteArrayAsync(host, Entry("BMK1Err404"));
        await WaitForAsync(() => Volatile.Read(ref delivered) > 0);

        await host.StopAsync();
    }

    [Fact]
    public async Task AcknowledgeAsync_DelegatesToTheRegisteredDialect()
    {
        var dialect = new RecordingDialect();
        var (host, monitor) = await StartAsync(services =>
            services.AddSingleton<IPlcAlarmDialect>(dialect));
        using var _ = host;

        await WriteArrayAsync(host, Entry("BMK1Err404"));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 1);

        var acknowledged = await monitor.AcknowledgeAsync(PlcId, "BMK1Err404", CancellationToken.None);

        Assert.True(acknowledged);
        Assert.Equal("BMK1Err404", dialect.LastAcknowledged?.Key);

        await host.StopAsync();
    }

    [Fact]
    public async Task AcknowledgeAsync_UnknownAlarm_DoesNotReachTheDialect()
    {
        var dialect = new RecordingDialect();
        var (host, monitor) = await StartAsync(services =>
            services.AddSingleton<IPlcAlarmDialect>(dialect));
        using var _ = host;

        var acknowledged = await monitor.AcknowledgeAsync(PlcId, "NoSuchAlarm", CancellationToken.None);

        Assert.False(acknowledged);
        Assert.Null(dialect.LastAcknowledged);

        await host.StopAsync();
    }

    [Fact]
    public async Task AcknowledgeAsync_UnknownTarget_DoesNotReachTheDialect()
    {
        // The monitor's other early return. A dialect is handed a connection and a target's
        // options, and there are neither for a plcId nobody configured — so this has to be
        // answered here rather than passed on.
        var dialect = new RecordingDialect();
        var (host, monitor) = await StartAsync(services =>
            services.AddSingleton<IPlcAlarmDialect>(dialect));
        using var _ = host;

        var acknowledged = await monitor.AcknowledgeAsync(
            "someOtherPlc", "BMK1Err404", CancellationToken.None);

        Assert.False(acknowledged);
        Assert.Null(dialect.LastAcknowledged);

        await host.StopAsync();
    }

    [Fact]
    public async Task AcknowledgeAsync_DialectDeclines_SurfacesFalse()
    {
        // The dialect's "the PLC has no such alarm" has to reach the caller as false. Folding
        // it into true — or into an exception — would tell an operator to stop asking, or to
        // retry, when neither is what happened.
        var dialect = new RecordingDialect { Result = false };
        var (host, monitor) = await StartAsync(services =>
            services.AddSingleton<IPlcAlarmDialect>(dialect));
        using var _ = host;

        await WriteArrayAsync(host, Entry("BMK1Err404"));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 1);

        var acknowledged = await monitor.AcknowledgeAsync(PlcId, "BMK1Err404", CancellationToken.None);

        Assert.False(acknowledged);

        // It really did reach the dialect — the false is the dialect's answer, not the
        // monitor's own "not outstanding" early return arriving at the same value.
        Assert.Equal("BMK1Err404", dialect.LastAcknowledged?.Key);

        await host.StopAsync();
    }

    [Fact]
    public async Task AcknowledgeAsync_DialectRefuses_PropagatesToTheCaller()
    {
        // IPlcAlarmMonitor.AcknowledgeAsync documents PlcAlarmAcknowledgeException as something
        // a caller can catch. Logging it and returning false instead would make that an empty
        // promise AND collapse "try again" into "it is gone".
        var dialect = new RecordingDialect
        {
            Failure = () => new PlcAlarmAcknowledgeException("PLC 'plc1' is BUSY.", "BUSY", 6),
        };
        var (host, monitor) = await StartAsync(services =>
            services.AddSingleton<IPlcAlarmDialect>(dialect));
        using var _ = host;

        await WriteArrayAsync(host, Entry("BMK1Err404"));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 1);

        var ex = await Assert.ThrowsAsync<PlcAlarmAcknowledgeException>(
            () => monitor.AcknowledgeAsync(PlcId, "BMK1Err404", CancellationToken.None));

        Assert.Equal("BUSY", ex.ReturnCodeName);

        await host.StopAsync();
    }

    [Fact]
    public async Task AcknowledgeAsync_LeavesAnAuditTrail_DistinguishingTheTwoOutcomes()
    {
        // On the reference rack a successful acknowledge REMOVES the entry, so the store sees
        // the alarm vanish and emits Ended, not Acknowledged. Without a log line here, "key Y
        // was acknowledged on PLC X at time T" is recorded nowhere at all — in an alarms
        // package, the one event an audit trail most wants.
        var log = new CapturingLoggerProvider();

        var acknowledging = new RecordingDialect();
        var (host, monitor) = await StartAsync(services =>
        {
            services.AddSingleton<ILoggerProvider>(log);
            services.AddSingleton<IPlcAlarmDialect>(acknowledging);
        });
        using var _ = host;

        await WriteArrayAsync(host, Entry("BMK1Err404"));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 1);

        await monitor.AcknowledgeAsync(PlcId, "BMK1Err404", CancellationToken.None);

        Assert.Contains(log.Entries, e =>
            e.StartsWith("Information: ", StringComparison.Ordinal) &&
            e.Contains("BMK1Err404", StringComparison.Ordinal) &&
            e.Contains(PlcId, StringComparison.Ordinal));

        await host.StopAsync();
    }

    [Fact]
    public async Task AcknowledgeAsync_Declined_IsLoggedAsSomethingOtherThanAnAcknowledgement()
    {
        // Both outcomes are logged, but an operator reading the log has to be able to tell
        // "the PLC acknowledged it" from "the PLC said it has no such alarm". One line for
        // both, or the same wording for both, is an audit trail that cannot be audited.
        var log = new CapturingLoggerProvider();

        var declining = new RecordingDialect { Result = false };
        var (host, monitor) = await StartAsync(services =>
        {
            services.AddSingleton<ILoggerProvider>(log);
            services.AddSingleton<IPlcAlarmDialect>(declining);
        });
        using var _ = host;

        await WriteArrayAsync(host, Entry("BMK1Err404"));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 1);

        await monitor.AcknowledgeAsync(PlcId, "BMK1Err404", CancellationToken.None);

        var line = Assert.Single(
            log.Entries, e => e.Contains("BMK1Err404", StringComparison.Ordinal));

        Assert.StartsWith("Information: ", line, StringComparison.Ordinal);

        // The success wording, which the declined path must NOT reuse.
        Assert.DoesNotContain("Acknowledged BMK1Err404", line, StringComparison.Ordinal);

        await host.StopAsync();
    }

    [Fact]
    public async Task Notifications_AreBoundByTheRegisteredDialect()
    {
        // The other half of the seam, and the only test in this class that discriminates it.
        // The shipped dialect's Bind IS PlcAlarmBinder.Bind, so a monitor that still called
        // the binder directly satisfies every other test here — including the ones using
        // RecordingDialect, which delegates its own binding to that same default. This one
        // binds something no binder could produce from the notification that was written.
        var dialect = new FixedBindingDialect("SomethingOnlyADialectWouldSay");
        var (host, monitor) = await StartAsync(services =>
            services.AddSingleton<IPlcAlarmDialect>(dialect));
        using var _ = host;

        await WriteArrayAsync(host, Entry("BMK1Err404"));
        await WaitForAsync(() => monitor.GetOutstanding().Count == 1);

        Assert.Equal(
            "SomethingOnlyADialectWouldSay", Assert.Single(monitor.GetOutstanding()).Key);

        await host.StopAsync();
    }

    /// <summary>
    /// Stands in for a consumer's own dialect: binds exactly as the shipped default does, so
    /// the whole notification pipeline still runs, and records the alarm it is asked to
    /// acknowledge instead of talking to a PLC.
    /// </summary>
    private sealed class RecordingDialect : IPlcAlarmDialect
    {
        private readonly ErrorHandlerAlarmDialect _binding = new();

        /// <summary>
        /// The alarm of the last acknowledgement, or <see langword="null"/> if this dialect
        /// was never asked to acknowledge anything.
        /// </summary>
        public PlcAlarm? LastAcknowledged { get; private set; }

        /// <summary>What <c>AcknowledgeAsync</c> answers — the PLC acknowledged it, or has no
        /// such alarm to acknowledge.</summary>
        public bool Result { get; init; } = true;

        /// <summary>
        /// When set, <c>AcknowledgeAsync</c> throws this instead of answering — a PLC refusing
        /// for any reason other than "no such alarm". A factory, so each call throws a fresh
        /// exception with its own stack.
        /// </summary>
        public Func<Exception>? Failure { get; init; }

        public IReadOnlyList<PlcAlarm> Bind(AlarmBindContext context) => _binding.Bind(context);

        public Task<bool> AcknowledgeAsync(AlarmAcknowledgeContext context, CancellationToken ct)
        {
            LastAcknowledged = context.Alarm;

            return Failure is { } failure
                ? Task.FromException<bool>(failure())
                : Task.FromResult(Result);
        }
    }

    /// <summary>
    /// Captures every log entry the host emits, so a test can assert on what an operator would
    /// find in the log afterwards.
    /// </summary>
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

    /// <summary>
    /// Binds one alarm under a caller-chosen key, whatever the notification actually carried.
    /// </summary>
    private sealed class FixedBindingDialect(string key) : IPlcAlarmDialect
    {
        public IReadOnlyList<PlcAlarm> Bind(AlarmBindContext context) =>
        [
            new PlcAlarm
            {
                Key = key,
                EquipmentId = "BMK1",
                ErrorCode = 404,
                Severity = AlarmSeverity.Error,
                IsActive = true,
                NeedsAcknowledgement = true,
                IsAcknowledged = false,
                PlcTimestamp = new DateTime(2026, 6, 17, 12, 0, 0),
                SlotIndex = 0,
                PlcId = context.PlcId,
            },
        ];

        public Task<bool> AcknowledgeAsync(AlarmAcknowledgeContext context, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
