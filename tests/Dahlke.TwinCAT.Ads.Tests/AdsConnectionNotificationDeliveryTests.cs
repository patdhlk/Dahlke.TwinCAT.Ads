using Microsoft.Extensions.Logging.Abstractions;
using TwinCAT.TypeSystem;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Pins <c>AdsConnection.DeliverDecodedContainerInBackground</c> — the offloaded half of the
/// <see cref="AdsNotification"/> subscription overload, which decodes a container notification off
/// the ADS notification thread and delivers it when the decode completes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this can run without hardware.</b> <c>AdsConnection.SubscribeAsync</c> itself needs a
/// connected <c>AdsClient</c> (<c>ReadSymbol</c>, <c>AddDeviceNotificationAsync</c>, the
/// <c>AdsNotification</c> event), so the registration path stays hardware-only. The offloaded
/// delivery body does not: it touches only <see cref="PlcValueDecoder"/>, the per-target timeout,
/// and the caller's callback. It is <c>internal</c> for exactly this reason — the same seam
/// reasoning as <c>IsContainer</c> and <c>SetSymbolLoaderForTesting</c> — so the disposal
/// behaviour below is verified by test rather than by inspection.
/// </para>
/// <para>
/// What is asserted here is the contract the untyped overload states and this overload inherits:
/// a callback must not fire after the subscription handle's disposal completes. Because a struct
/// decode spans one ADS read per member, an offload that ignored disposal could deliver into a
/// consumer that tore its sink down many round-trips ago.
/// </para>
/// </remarks>
public class AdsConnectionNotificationDeliveryTests
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Bounded window for a NEGATIVE assertion ("nothing was delivered"). A negative needs some
    /// settle time by nature; this mirrors the contract suite's <c>SettleDelay</c> idiom and is
    /// never used as the primary synchronisation mechanism — every positive assertion below waits
    /// on a <see cref="TaskCompletionSource"/> instead.
    /// </summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Real-time bound for "the in-flight read was aborted by DISPOSAL, not by the decode's own
    /// timeout". Generous enough not to be flaky on a loaded machine, but far below the 60s
    /// per-target timeout the relevant test configures.
    /// </summary>
    private static readonly TimeSpan PromptAbort = TimeSpan.FromSeconds(5);

    private static readonly DateTimeOffset PlcTimestamp =
        new(2026, 7, 28, 9, 30, 15, TimeSpan.FromHours(2));

    /// <summary>
    /// Stands in for the top-level read the notification handler SKIPS for a struct/function block
    /// with sub-symbols — the decoder only null-checks it. Mirrors <c>AdsConnection</c>'s own
    /// <c>SkippedReadPlaceholder</c>.
    /// </summary>
    private static readonly object SkippedReadPlaceholder = new();

    private static AdsConnection CreateConnection(int timeoutMs = 3000) =>
        new("plc1", new PlcTargetOptions { DisplayName = "PLC 1", TimeoutMs = timeoutMs }, new NullLoggerFactory());

    [Fact]
    public async Task Delivers_the_decoded_tree_with_the_registration_type_name_and_the_plc_timestamp()
    {
        var motor = new StubSymbol(DataTypeCategory.Struct, "ST_Motor",
            new StubValueSymbol("Speed", DataTypeCategory.Primitive, "INT", 1500),
            new StubValueSymbol("Running", DataTypeCategory.Primitive, "BOOL", true));
        var connection = CreateConnection();
        using var disposal = new CancellationTokenSource();

        var delivered = new TaskCompletionSource<AdsNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.DeliverDecodedContainerInBackground(
            "MAIN.Motor", SkippedReadPlaceholder, motor, "ST_Motor", PlcTimestamp,
            n => delivered.TrySetResult(n), disposal.Token);

        var notification = await delivered.Task.WaitAsync(RealTimeout);

        Assert.Equal("MAIN.Motor", notification.SymbolPath);
        Assert.Equal("ST_Motor", notification.TypeName);

        // The timestamp is the one captured on the notification thread before the hand-off — NOT
        // the moment the decode finished.
        Assert.Equal(PlcTimestamp, notification.Timestamp);

        var tree = Assert.IsType<Dictionary<string, object?>>(notification.Value);
        Assert.Equal(1500, tree["Speed"]);
        Assert.Equal(true, tree["Running"]);
    }

    [Fact]
    public async Task Dispose_aborts_an_in_flight_member_read_and_drops_the_delivery()
    {
        // This member's read never completes unless its token is cancelled, so the decode is
        // guaranteed to still be in flight when the handle is disposed.
        var speed = StubValueSymbol.ThatNeverCompletesRead("Speed", DataTypeCategory.Primitive, "INT");
        var motor = new StubSymbol(DataTypeCategory.Struct, "ST_Motor", speed);

        // A deliberately huge per-target timeout. The decode's own timeout budget would ALSO
        // eventually cancel this read, so without this the test could pass on the timeout rather
        // than on disposal. At 60s, a cancellation observed inside PromptAbort (below) can only
        // have come from the disposal token. Same technique as PlcValueDecoderTests'
        // "...stops_a_slow_struct_members_read_promptly".
        var connection = CreateConnection(timeoutMs: 60_000);
        using var disposal = new CancellationTokenSource();

        var delivered = new TaskCompletionSource<AdsNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.DeliverDecodedContainerInBackground(
            "MAIN.Motor", SkippedReadPlaceholder, motor, "ST_Motor", PlcTimestamp,
            n => delivered.TrySetResult(n), disposal.Token);

        // Disposing the subscription handle cancels this token.
        await disposal.CancelAsync();

        // The disposal token is LINKED INTO the decode, so the in-flight member read is actually
        // aborted rather than left running to completion for a value nobody will receive. Waiting
        // on this (instead of a delay) is what distinguishes "aborted" from "still hanging", and
        // the tight bound is what distinguishes "aborted by disposal" from "aborted 60s later by
        // the decode's own timeout".
        await speed.ReadCancelled.WaitAsync(PromptAbort);

        // And nothing was delivered.
        await Task.Delay(SettleDelay);
        Assert.False(delivered.Task.IsCompleted);
    }

    [Fact]
    public async Task Does_not_deliver_when_the_handle_was_disposed_before_the_decode_finished()
    {
        // An array with no sub-symbols decodes by copying raw elements — no ADS reads at all, so
        // NOTHING inside the decode observes cancellation. The decode therefore succeeds even
        // though the handle is already disposed, which leaves the explicit pre-delivery check as
        // the only thing that can suppress the callback. That is exactly what this pins.
        var symbol = new StubSymbol(DataTypeCategory.Array, "ARRAY [0..1] OF INT");
        var connection = CreateConnection();
        using var disposal = new CancellationTokenSource();
        await disposal.CancelAsync();

        var delivered = new TaskCompletionSource<AdsNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.DeliverDecodedContainerInBackground(
            "MAIN.Values", new[] { 10, 20 }, symbol, "ARRAY [0..1] OF INT", PlcTimestamp,
            n => delivered.TrySetResult(n), disposal.Token);

        await Task.Delay(SettleDelay);
        Assert.False(delivered.Task.IsCompleted);
    }

    [Fact]
    public async Task A_throwing_callback_does_not_stop_the_next_notification_being_delivered()
    {
        // The subscription contract: a throwing callback must not tear the subscription down. The
        // deliveries are independent tasks, so this asserts the observable consequence — a second
        // notification for the same symbol still arrives after the first callback threw.
        var motor = new StubSymbol(DataTypeCategory.Struct, "ST_Motor",
            new StubValueSymbol("Speed", DataTypeCategory.Primitive, "INT", 1500));
        var connection = CreateConnection();
        using var disposal = new CancellationTokenSource();

        var threw = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.DeliverDecodedContainerInBackground(
            "MAIN.Motor", SkippedReadPlaceholder, motor, "ST_Motor", PlcTimestamp,
            _ =>
            {
                threw.TrySetResult();
                throw new InvalidOperationException("subscriber blew up");
            },
            disposal.Token);
        await threw.Task.WaitAsync(RealTimeout);

        var second = new TaskCompletionSource<AdsNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.DeliverDecodedContainerInBackground(
            "MAIN.Motor", SkippedReadPlaceholder, motor, "ST_Motor", PlcTimestamp,
            n => second.TrySetResult(n), disposal.Token);

        var notification = await second.Task.WaitAsync(RealTimeout);
        Assert.Equal("MAIN.Motor", notification.SymbolPath);
    }
}
