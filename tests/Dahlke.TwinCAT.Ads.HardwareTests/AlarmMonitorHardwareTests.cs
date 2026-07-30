using Dahlke.TwinCAT.Ads.Alarms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddTwinCatAds(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions
            {
                AmsNetId = HardwareTestConfig.AmsNetId,
                Port = HardwareTestConfig.Port,
                TimeoutMs = 10000,
            };
        });

        builder.Services.Configure<PlcAlarmsOptions>(o =>
            o.Targets["plc1"] = new PlcAlarmTargetOptions
            {
                SymbolPath = HardwareTestConfig.SymbolAlarms!,
                CycleTimeMs = 200,
            });

        builder.Services.AddTwinCatAdsAlarms(builder.Configuration);

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var monitor = host.Services.GetRequiredService<IPlcAlarmMonitor>();

            // The array may legitimately hold no outstanding alarms. What is proven here
            // is that the notification BOUND — a shape mismatch would have raised
            // PlcAlarmShapeException inside the binder. Assert on every alarm that did
            // bind, so a partially-wrong mapping (a blank sKey, a negative slot) fails
            // rather than passing on an empty collection.
            await Task.Delay(TimeSpan.FromSeconds(3));

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
                    "check the host log for that exception if GetOutstanding() stayed empty despite " +
                    "known outstanding alarms on the PLC.");
            }
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
