using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Generic <see cref="IAdsConnection.SubscribeAsync{T}"/> — typed value-change
/// notifications.
///
/// Each notification value is converted to <typeparamref name="T"/> using the SAME
/// rules as <see cref="IAdsConnection.ReadValueAsync{T}"/>
/// (<see cref="System.Convert.ChangeType(object, System.Type, System.IFormatProvider)"/>
/// with <see cref="System.Globalization.CultureInfo.InvariantCulture"/>). A value that
/// fails conversion is DROPPED (Warning logged, callback not invoked). A null value with
/// a value-type T is dropped; a null value with a reference/nullable T invokes the
/// callback with null.
///
/// Durability and threading semantics are identical to the untyped overload: typed
/// subscriptions made through the facade survive reconnects.
/// </summary>
public class TypedSubscribeTests
{
    private static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(15);

    private static SimulatedAdsConnection CreateSim()
        => new("test-plc", "Test PLC", NullLoggerFactory.Instance);

    private static async Task WaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + RealTimeout;
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Predicate did not become true within the real-time guard window.");
            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }
    }

    // =========================================================================
    // SimulatedAdsConnection — typed notifications
    // =========================================================================

    [Fact]
    public async Task TypedSubscribe_Int_ReceivesTypedValue()
    {
        using var conn = CreateSim();

        string? receivedPath = null;
        int received = 0;
        var fired = 0;
        using var sub = await conn.SubscribeAsync<int>(
            "A.x", 100, (p, v) => { receivedPath = p; received = v; fired++; }, CancellationToken.None);

        await conn.WriteValueAsync("A.x", 42, CancellationToken.None);

        Assert.Equal(1, fired);
        Assert.Equal("A.x", receivedPath);
        Assert.Equal(42, received);
    }

    [Fact]
    public async Task TypedSubscribe_StringToInt_Converts()
    {
        using var conn = CreateSim();

        int received = 0;
        var fired = 0;
        using var sub = await conn.SubscribeAsync<int>(
            "A.x", 100, (_, v) => { received = v; fired++; }, CancellationToken.None);

        // A string-seeded notification value "42" converts to int 42 (InvariantCulture).
        await conn.WriteValueAsync("A.x", "42", CancellationToken.None);

        Assert.Equal(1, fired);
        Assert.Equal(42, received);
    }

    [Fact]
    public async Task TypedSubscribe_IncompatibleType_CallbackSkipped()
    {
        using var conn = CreateSim();

        var fired = 0;
        using var sub = await conn.SubscribeAsync<int>(
            "A.x", 100, (_, _) => fired++, CancellationToken.None);

        // A Guid cannot be converted to int -> notification is dropped (warned, not invoked).
        await conn.WriteValueAsync("A.x", Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(0, fired);
    }

    [Fact]
    public async Task TypedSubscribe_NullWithValueType_Skipped()
    {
        using var conn = CreateSim();

        var fired = 0;
        using var sub = await conn.SubscribeAsync<int>(
            "A.x", 100, (_, _) => fired++, CancellationToken.None);

        // Seed a non-null value so the subsequent null write registers as a change.
        await conn.WriteValueAsync("A.x", 1, CancellationToken.None);
        Assert.Equal(1, fired); // the int 1 was delivered

        // Now write null: int is a non-nullable value type -> dropped.
        await conn.WriteValueAsync("A.x", (object)null!, CancellationToken.None);

        Assert.Equal(1, fired); // still 1 — the null was dropped
    }

    [Fact]
    public async Task TypedSubscribe_NullWithNullableString_InvokedWithNull()
    {
        using var conn = CreateSim();

        string? received = "sentinel";
        var fired = 0;
        using var sub = await conn.SubscribeAsync<string?>(
            "A.x", 100, (_, v) => { received = v; fired++; }, CancellationToken.None);

        // Seed a non-null value so the null write counts as a change.
        await conn.WriteValueAsync("A.x", "hello", CancellationToken.None);
        Assert.Equal(1, fired);
        Assert.Equal("hello", received);

        // Write null: string is a reference type -> callback invoked with null.
        await conn.WriteValueAsync("A.x", (object)null!, CancellationToken.None);

        Assert.Equal(2, fired);
        Assert.Null(received);
    }

    [Fact]
    public async Task TypedSubscribe_Dispose_StopsFurtherCallbacks()
    {
        using var conn = CreateSim();

        var fired = 0;
        var sub = await conn.SubscribeAsync<int>(
            "A.x", 100, (_, _) => fired++, CancellationToken.None);

        await conn.WriteValueAsync("A.x", 1, CancellationToken.None);
        Assert.Equal(1, fired);

        sub.Dispose();
        await conn.WriteValueAsync("A.x", 2, CancellationToken.None);

        Assert.Equal(1, fired); // no callback after dispose
    }

    [Fact]
    public async Task TypedSubscribe_Dispose_IsIdempotent()
    {
        using var conn = CreateSim();

        var sub = await conn.SubscribeAsync<int>("A.x", 100, (_, _) => { }, CancellationToken.None);

        sub.Dispose();
        sub.Dispose();
        sub.Dispose();
    }

    [Fact]
    public async Task TypedSubscribe_NumericWidening_IntToDouble()
    {
        using var conn = CreateSim();

        double received = 0;
        var fired = 0;
        using var sub = await conn.SubscribeAsync<double>(
            "A.x", 100, (_, v) => { received = v; fired++; }, CancellationToken.None);

        await conn.WriteValueAsync("A.x", 7, CancellationToken.None); // int box -> widened to double

        Assert.Equal(1, fired);
        Assert.Equal(7.0, received);
    }

    // =========================================================================
    // Facade — typed subscriptions are durable across reconnects
    // =========================================================================

    [Fact]
    public async Task TypedSubscription_IsDurable_SurvivesReconnect()
    {
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1", new PlcTargetOptions { DisplayName = "PLC One", TimeoutMs = 5000 },
            time, NullLogger.Instance);

        var first = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(first);

        var received = new List<int>();
        var handle = await facade.SubscribeAsync<int>(
            "MAIN.x", 100, (_, v) => received.Add(v), CancellationToken.None).WaitAsync(RealTimeout);

        // The underlying connection saw exactly one (already-wrapped, untyped) registration.
        Assert.Single(first.Subscriptions);
        first.Subscriptions.Single().FireNotification(42);
        Assert.Equal([42], received);

        // Reconnect: a new connection is published. The facade re-registers the
        // already-wrapped untyped callback for free.
        var second = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(second);

        await WaitUntil(() => second.Subscriptions.Count == 1);

        // Conversion still applies on the new connection: a string-seeded "99" converts.
        second.Subscriptions.Single().FireNotification("99");
        Assert.Equal([42, 99], received);

        // And an incompatible value is dropped on the new connection too.
        second.Subscriptions.Single().FireNotification(Guid.NewGuid());
        Assert.Equal([42, 99], received);

        handle.Dispose();
    }

    [Fact]
    public async Task FacadeTyped_IncompatibleValue_Dropped()
    {
        var time = new FakeTimeProvider();
        var facade = new AdsConnectionFacade(
            "plc1", new PlcTargetOptions { DisplayName = "PLC One", TimeoutMs = 5000 },
            time, NullLogger.Instance);

        var conn = new FakeManagedConnection("plc1") { IsConnected = true };
        facade.SetCurrent(conn);

        var fired = 0;
        var handle = await facade.SubscribeAsync<int>(
            "MAIN.x", 100, (_, _) => fired++, CancellationToken.None).WaitAsync(RealTimeout);

        conn.Subscriptions.Single().FireNotification(Guid.NewGuid());
        Assert.Equal(0, fired);

        conn.Subscriptions.Single().FireNotification(5);
        Assert.Equal(1, fired);

        handle.Dispose();
    }
}
