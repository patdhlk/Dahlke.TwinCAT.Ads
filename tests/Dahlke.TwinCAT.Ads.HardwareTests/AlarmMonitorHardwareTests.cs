using System.Collections.Concurrent;
using Dahlke.TwinCAT.Ads.Alarms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.HardwareTests;

/// <summary>
/// The ONLY test that proves the binder against a real ADS notification payload.
/// </summary>
/// <remarks>
/// <para>
/// Every other alarm test builds its own input, so all of them would stay green if the
/// real notification shape differed from what <c>PlcAlarmBinder</c> expects. This test
/// closes that gap — and it <b>skips in CI</b>, where no PLC exists. A green CI run is
/// therefore NOT evidence that binding works against hardware.
/// </para>
/// <para>
/// <b>The inner gate is a second trap for the same reason.</b> With
/// <c>TWINCAT_HARDWARE_TESTS=1</c> set but <c>TWINCAT_TEST_SYMBOL_ALARMS</c> unset, this
/// test returns immediately and reports as <b>passed</b> — not skipped — having done
/// nothing at all. That is this project's established convention for a symbol-specific
/// test (see <see cref="HardwareTestConfig.HasSymbolInt"/> and its callers), but it means
/// a green run of THIS test is only coverage evidence when
/// <c>TWINCAT_TEST_SYMBOL_ALARMS</c> was actually set on the machine that ran it.
/// </para>
/// <para>
/// <b>Three ways this test can fail, deliberately, so a pass cannot be vacuous.</b> An
/// empty outstanding-alarm array is a legitimate PLC state, but by itself it is
/// indistinguishable from "the binder threw and the whole snapshot was silently dropped":
/// <c>PlcAlarmMonitor</c> catches <c>PlcAlarmShapeException</c>, logs it, and keeps the
/// store's last good reading — empty, on a fresh host — so
/// <see cref="IPlcAlarmMonitor.GetOutstanding()"/> alone cannot tell the two apart. This
/// test therefore also (1) opens its own raw subscription to the same symbol, independent
/// of the monitor, and asserts at least one notification arrived — proving the pipeline
/// actually ran, since ADS delivers one notification on registration — and (2) captures
/// Error-level log entries from <c>PlcAlarmMonitor</c>'s own logger category and asserts
/// none were recorded, which fails on a shape mismatch or on <c>ErrorType</c> arriving as
/// <c>TwinCAT.TypeSystem.DynamicEnumValue</c> rather than a plain integral REGARDLESS of
/// whether any alarm ended up outstanding. Only the third failure mode — a bound alarm
/// with an out-of-range <see cref="AlarmSeverity"/> — depends on the array being
/// non-empty; see the assertion inside the loop below.
/// </para>
/// <para>
/// <b>What this test does NOT prove, even when it passes with a symbol configured.</b> It
/// exercises only the read/bind path of one array notification. It does NOT prove that
/// acknowledgement reaches the PLC: nothing here calls
/// <see cref="IPlcAlarmMonitor.AcknowledgeAsync"/> or checks that the PLC acted on one.
/// That path is covered separately by <see cref="Acknowledge_ReachesThePlc"/>, because
/// acknowledgement is not a write — it invokes the PLC method named by
/// <c>AcknowledgeMethod</c> on the function block derived from <c>SymbolPath</c>, and
/// reads the returned <c>deaReturnType</c> BY NAME against the members the PLC publishes.
/// So it has to assert on the method call and its result, and on the array afterwards no
/// longer carrying the alarm — writing <c>IsAcked</c> and reading it back proves nothing,
/// since that is the mechanism the hardware disproved. The health check and the text
/// catalog are likewise unexercised here.
/// </para>
/// <para>
/// Requires a PLC with an <c>ARRAY[..] OF ST_ErrorEntry</c> named by
/// <c>TWINCAT_TEST_SYMBOL_ALARMS</c>, alongside the variables
/// <see cref="HardwareTestConfig"/> already requires.
/// </para>
/// </remarks>
public class AlarmMonitorHardwareTests
{
    [HardwareFact]
    public async Task AlarmArray_BindsAgainstRealHardware()
    {
        if (!HardwareTestConfig.HasSymbolAlarms)
            return;

        var symbol = HardwareTestConfig.SymbolAlarms!;
        var capturedErrors = new ConcurrentQueue<string>();

        var builder = Host.CreateApplicationBuilder();

        // Records PlcAlarmMonitor's own Error-level log entries so this test can fail on a
        // thrown binder even when GetOutstanding() comes back empty — see the class remarks.
        builder.Logging.AddProvider(new CapturingLoggerProvider(capturedErrors));

        builder.Services.AddTwinCatAds(o =>
        {
            HardwareTestOptionsConfigurator.ConfigureTarget(o, "plc1", new PlcTargetOptions
            {
                AmsNetId = HardwareTestConfig.AmsNetId,
                Port = HardwareTestConfig.Port,
                TimeoutMs = 10000,
            });
        });

        builder.Services.Configure<PlcAlarmsOptions>(o =>
            o.Targets["plc1"] = new PlcAlarmTargetOptions
            {
                SymbolPath = symbol,
                CycleTimeMs = 200,
            });

        builder.Services.AddTwinCatAdsAlarms(builder.Configuration);

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var monitor = host.Services.GetRequiredService<IPlcAlarmMonitor>();
            var pool = host.Services.GetRequiredService<IAdsConnectionPool>();

            // Prove the pipeline actually ran: a raw subscription to the same symbol,
            // independent of the monitor's own. A test that observes nothing must not be
            // able to look identical to one that observed a clean array.
            var notificationCount = 0;

            using var subscribeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var rawSubscription = await pool.GetConnection("plc1").SubscribeAsync(
                symbol,
                cycleTimeMs: 200,
                callback: (_, _) => Interlocked.Increment(ref notificationCount),
                ct: subscribeCts.Token);

            // The array may legitimately hold no outstanding alarms. What is proven here
            // is that the notification BOUND — a shape mismatch would have raised
            // PlcAlarmShapeException inside the binder. Assert on every alarm that did
            // bind, so a partially-wrong mapping (a blank sKey, a negative slot) fails
            // rather than passing on an empty collection.
            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.True(
                notificationCount >= 1,
                $"No ADS notification arrived on '{symbol}' during the observation window, even " +
                "though TwinCAT delivers one on registration. Zero notifications means the symbol " +
                "path is wrong or the subscription never registered, so every assertion below " +
                "would have proven nothing about binding.");

            Assert.True(
                capturedErrors.IsEmpty,
                "PlcAlarmMonitor logged at least one Error-level entry while this test was " +
                "observing it. This is asserted separately from GetOutstanding() because a " +
                "thrown binder drops the whole snapshot silently, leaving an empty outstanding " +
                "set that is otherwise indistinguishable from a genuinely empty array. Likely " +
                "causes: a shape mismatch (PlcAlarmShapeException, whose own message names the " +
                "offending member and symbol path), or ErrorType arriving as " +
                "TwinCAT.TypeSystem.DynamicEnumValue instead of a plain integral. Captured: " +
                string.Join(" | ", capturedErrors));

            foreach (var alarm in monitor.GetOutstanding())
            {
                Assert.False(string.IsNullOrWhiteSpace(alarm.Key));
                Assert.Equal("plc1", alarm.PlcId);
                Assert.True(alarm.SlotIndex >= 0);

                // The residual unknown this package cannot verify without real hardware:
                // E_ErrorType is a PLC enum. NotificationPayload.cs documents that the
                // value factory "unmarshals primitives, strings, sub-ranges and enums in
                // place", which implies ErrorType arrives as a plain integral — but
                // TwinCAT.TypeSystem.DynamicEnumValue exists in the pinned assembly and is
                // NOT IConvertible. Assert every bound alarm's Severity is one of the four
                // known AlarmSeverity members, so a PLC-side numbering mismatch (or a
                // partially-successful DynamicEnumValue surprise) fails loudly here instead
                // of shipping a silently-wrong severity.
                Assert.True(
                    alarm.Severity is AlarmSeverity.None or AlarmSeverity.Info
                        or AlarmSeverity.Warning or AlarmSeverity.Error,
                    $"Alarm '{alarm.Key}' bound with Severity={(int)alarm.Severity}, which is not " +
                    "one of the four known AlarmSeverity values (None=0, Info=1, Warning=2, " +
                    "Error=3). An out-of-range or failed severity means the PLC's ErrorType did " +
                    "not arrive as a plain integral matching this package's assumed E_ErrorType " +
                    "numbering — the likely cause is TwinCAT.TypeSystem.DynamicEnumValue, which " +
                    "is not IConvertible. Note that if ErrorType arrives as DynamicEnumValue for " +
                    "every entry, PlcAlarmBinder.Read<int> throws PlcAlarmShapeException before " +
                    "this alarm is ever constructed, and the whole snapshot is dropped instead — " +
                    "the capturedErrors assertion above is what catches THAT case, since " +
                    "GetOutstanding() would stay empty despite known outstanding alarms on the PLC.");
            }
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// The only test that proves acknowledgement reaches the PLC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Writing <c>IsAcked</c> on the entry — what this package did before — provably does
    /// nothing on a PLC whose function block rebuilds the array each scan: the write is
    /// overwritten within one cycle. That failure was invisible because the call still
    /// reported success. This test asserts on what the PLC itself returns.
    /// </para>
    /// <para>
    /// It also cannot be defeated by the alarm re-firing, which is what made the earlier
    /// attempt inconclusive: the method's return value reports the outcome regardless of what
    /// the array does next. On this rig the test alarm re-fires every second or two, so an
    /// alarm can legitimately end between reading it and the call landing — either
    /// <c>AcknowledgeAsync</c> returning <see langword="true"/>, or the alarm no longer being
    /// outstanding, means the acknowledgement was handled. What must NOT happen is a throw:
    /// that means the PLC refused, and <see cref="PlcAlarmAcknowledgeException"/> carries the
    /// reason.
    /// </para>
    /// <para>
    /// Requires <c>TWINCAT_TEST_ALARM_ACK_KEY</c> in addition to the read-path variables,
    /// because it changes machine state — and, like every other symbol-gated test in this
    /// class, reports <b>passed</b> (not skipped) having done nothing when that variable is
    /// unset, even with <c>TWINCAT_HARDWARE_TESTS=1</c> set. A green run is only coverage
    /// evidence when <c>TWINCAT_TEST_ALARM_ACK_KEY</c> was actually set on the machine that
    /// ran it.
    /// </para>
    /// </remarks>
    [HardwareFact]
    public async Task Acknowledge_ReachesThePlc()
    {
        if (!HardwareTestConfig.HasSymbolAlarms || !HardwareTestConfig.HasAlarmAckKey)
            return;

        var symbol = HardwareTestConfig.SymbolAlarms!;

        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddTwinCatAds(o =>
        {
            HardwareTestOptionsConfigurator.ConfigureTarget(o, "plc1", new PlcTargetOptions
            {
                AmsNetId = HardwareTestConfig.AmsNetId,
                Port = HardwareTestConfig.Port,
                TimeoutMs = 10000,
            });
        });

        builder.Services.Configure<PlcAlarmsOptions>(o =>
            o.Targets["plc1"] = new PlcAlarmTargetOptions
            {
                SymbolPath = symbol,
                CycleTimeMs = 200,
            });

        builder.Services.AddTwinCatAdsAlarms(builder.Configuration);

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var monitor = host.Services.GetRequiredService<IPlcAlarmMonitor>();
            var key = HardwareTestConfig.AlarmAckKey!;

            // The enum must resolve from the running program — this is the assertion that
            // would have caught a hardcoded numbering, since the rack and its source disagree
            // (the rack publishes deaReturnType as ERROR=0 ... SUCCESS=5, while the source
            // that ships with the project now reads SUCCESS=0).
            var conn = host.Services.GetRequiredService<IAdsConnectionPool>().GetConnection("plc1");
            var members = await conn.GetEnumMembersAsync("deaReturnType", CancellationToken.None);
            Assert.Contains(members, m => m.Name == "SUCCESS");

            // Either outcome is legitimate: true if the PLC acknowledged, false if the alarm
            // had already ended — the rig re-fires every test alarm every second or two, so an
            // alarm can legitimately end between reading it and this call landing. On real
            // hardware a successful acknowledge REMOVES the entry from the array, so the store
            // emits Ended, never Acknowledged — do not assert on an Acknowledged transition or
            // on IsAcked; nothing writes it. What must NOT happen is a throw — that means the
            // PLC refused, and PlcAlarmAcknowledgeException carries the reason.
            var acknowledged = await monitor.AcknowledgeAsync("plc1", key, CancellationToken.None);

            Assert.True(
                acknowledged || monitor.GetOutstanding("plc1").All(a => a.Key != key),
                $"AcknowledgeAsync returned false for '{key}', but it is still outstanding — the " +
                "PLC neither acknowledged it nor reported it gone.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// Captures Error-level (or above) log entries from <c>PlcAlarmMonitor</c>'s own
    /// logger category. <c>PlcAlarmMonitor</c> is internal to
    /// <c>Dahlke.TwinCAT.Ads.Alarms</c> and this assembly has no
    /// <c>InternalsVisibleTo</c> grant for it, so the category is matched by name — the
    /// same full type name <see cref="ILogger{TCategoryName}"/> always uses — rather than
    /// via <see langword="typeof"/>.
    /// </summary>
    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> captured) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, captured);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string categoryName, ConcurrentQueue<string> captured) : ILogger
        {
            private const string MonitorCategory = "Dahlke.TwinCAT.Ads.Alarms.PlcAlarmMonitor";

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) =>
                logLevel >= LogLevel.Error && categoryName == MonitorCategory;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                    return;

                captured.Enqueue(exception is null
                    ? formatter(state, exception)
                    : $"{formatter(state, exception)} ({exception.GetType().Name}: {exception.Message})");
            }
        }
    }
}
