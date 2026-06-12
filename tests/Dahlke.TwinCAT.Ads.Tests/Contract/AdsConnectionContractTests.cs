using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Tests.Contract;

/// <summary>
/// ONE shared behavioural contract for <see cref="IAdsConnection"/>, run against every
/// implementation a consumer actually holds. Derived classes supply a
/// <see cref="ContractHarness"/> via <see cref="CreateHarnessAsync"/>; the [Fact]s here are
/// inherited and run once per derived class.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Consumers hold an <see cref="IAdsConnection"/> that is either the
/// pool's <see cref="AdsConnectionFacade"/> (wrapping a real or simulated managed connection)
/// or, in tests/direct use, the <see cref="SimulatedAdsConnection"/> itself. Past drift between
/// these two — sim subscriptions silently no-op'ing, mismatched missing-symbol exception types —
/// was caught only by human review. This suite encodes the C18/C16/C20 alignment as an
/// executable contract so the two implementations cannot drift apart silently: a behavioural
/// change in one that the other does not match fails a shared [Fact].
/// </para>
/// <para>
/// <b>The two implementations under test</b> (see the two derived classes):
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="SimulatedAdsConnectionContractTests"/> — the harness IS a
///     <see cref="SimulatedAdsConnection"/>, exercised directly.
///   </description></item>
///   <item><description>
///     <see cref="FacadeContractTests"/> — the harness is an <see cref="AdsConnectionFacade"/>
///     with an independent store-backed <c>InMemoryManagedConnection</c> pushed via
///     <c>SetCurrent</c>. The in-memory double mirrors the documented data-plane semantics
///     WITHOUT sharing the sim's code, so the suite pins BOTH the facade plumbing AND the
///     documented data-plane spec against the same assertions.
///   </description></item>
/// </list>
/// <para>
/// <b>Known, deliberate divergence from a REAL connection — batch missing symbols.</b>
/// For batch reads, a missing symbol yields <see cref="AdsValueResult.Success"/> with a
/// <see langword="null"/> value in BOTH implementations pinned here (sim and in-memory),
/// mirroring the untyped single-read. A REAL hardware connection instead records a per-symbol
/// <see cref="AdsValueResult.Failure"/> carrying <see cref="AdsErrorCode.DeviceSymbolNotFound"/>.
/// This contract pins the sim/in-memory semantics by design (per the C20 decision); the real
/// connection's divergent batch behaviour is verified separately against hardware and is
/// intentionally NOT asserted here.
/// </para>
/// <para>
/// <b>State transitions are out of scope.</b> <see cref="IAdsConnection.State"/> and
/// <see cref="IAdsConnection.IsConnected"/> are pinned as readable, but the facade-specific
/// transition behaviour (wait-then-throw, reconnect re-registration, stopped fast-fail) is
/// covered by the facade-specific suites, not by this shared contract — a simulated connection
/// has no transitions to assert.
/// </para>
/// </remarks>
public abstract class AdsConnectionContractTests
{
    /// <summary>
    /// Creates a fresh harness for one [Fact]. Each call must return an independent
    /// connection with empty state.
    /// </summary>
    protected abstract Task<ContractHarness> CreateHarnessAsync();

    // =====================================================================
    // Untyped read/write round-trip.
    // =====================================================================

    [Fact]
    public async Task UntypedWriteThenRead_RoundTripsValue()
    {
        await using var h = await CreateHarnessAsync();

        await h.Connection.WriteValueAsync("MAIN.x", 42, CancellationToken.None);
        var value = await h.Connection.ReadValueAsync("MAIN.x", CancellationToken.None);

        Assert.Equal(42, value);
    }

    [Fact]
    public async Task UntypedRead_OfNeverWrittenPath_ReturnsNull()
    {
        await using var h = await CreateHarnessAsync();

        var value = await h.Connection.ReadValueAsync("MAIN.never", CancellationToken.None);

        Assert.Null(value);
    }

    // =====================================================================
    // Typed read: exact, widening, string-seeded.
    // =====================================================================

    [Fact]
    public async Task TypedRead_ExactType_ReturnsValue()
    {
        await using var h = await CreateHarnessAsync();
        await h.WriteRawAsync("MAIN.i", 7);

        var value = await h.Connection.ReadValueAsync<int>("MAIN.i", CancellationToken.None);

        Assert.Equal(7, value);
    }

    [Fact]
    public async Task TypedRead_WideningIntToDouble_Converts()
    {
        await using var h = await CreateHarnessAsync();
        await h.WriteRawAsync("MAIN.i", 42);

        var value = await h.Connection.ReadValueAsync<double>("MAIN.i", CancellationToken.None);

        Assert.Equal(42.0, value);
    }

    [Fact]
    public async Task TypedRead_StringSeeded_ParsesToInt()
    {
        await using var h = await CreateHarnessAsync();
        await h.WriteRawAsync("MAIN.s", "42");

        var value = await h.Connection.ReadValueAsync<int>("MAIN.s", CancellationToken.None);

        Assert.Equal(42, value);
    }

    [Fact]
    public async Task TypedRead_MissingSymbol_ThrowsAdsErrorException_SymbolNotFound()
    {
        await using var h = await CreateHarnessAsync();

        var ex = await Assert.ThrowsAsync<AdsErrorException>(
            () => h.Connection.ReadValueAsync<int>("MAIN.missing", CancellationToken.None));

        Assert.Equal(AdsErrorCode.DeviceSymbolNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task TypedRead_ConversionFailure_ThrowsInvalidCast_WithPathAndTypes()
    {
        await using var h = await CreateHarnessAsync();
        await h.WriteRawAsync("MAIN.word", "not-a-number");

        var ex = await Assert.ThrowsAsync<InvalidCastException>(
            () => h.Connection.ReadValueAsync<int>("MAIN.word", CancellationToken.None));

        Assert.Contains("MAIN.word", ex.Message);
        Assert.Contains(nameof(Int32), ex.Message);
        Assert.Contains(nameof(String), ex.Message);
    }

    // =====================================================================
    // Typed write/read round-trip.
    // =====================================================================

    [Fact]
    public async Task TypedWriteThenTypedRead_RoundTripsValue()
    {
        await using var h = await CreateHarnessAsync();

        await h.Connection.WriteValueAsync<double>("MAIN.d", 3.5, CancellationToken.None);
        var value = await h.Connection.ReadValueAsync<double>("MAIN.d", CancellationToken.None);

        Assert.Equal(3.5, value);
    }

    // =====================================================================
    // Batch read.
    // =====================================================================

    [Fact]
    public async Task BatchRead_ReturnsPerSymbolResults()
    {
        await using var h = await CreateHarnessAsync();
        await h.WriteRawAsync("MAIN.a", 1);
        await h.WriteRawAsync("MAIN.b", 2);

        var results = await h.Connection.ReadValuesAsync(["MAIN.a", "MAIN.b"], CancellationToken.None);

        Assert.True(results["MAIN.a"].Succeeded);
        Assert.Equal(1, results["MAIN.a"].Value);
        Assert.True(results["MAIN.b"].Succeeded);
        Assert.Equal(2, results["MAIN.b"].Value);
    }

    [Fact]
    public async Task BatchRead_MissingSymbol_YieldsSuccessNull()
    {
        // Documented in-memory/sim semantic (NOT the real connection's — see class docs).
        await using var h = await CreateHarnessAsync();

        var results = await h.Connection.ReadValuesAsync(["MAIN.gone"], CancellationToken.None);

        Assert.True(results["MAIN.gone"].Succeeded);
        Assert.Null(results["MAIN.gone"].Value);
    }

    // =====================================================================
    // Batch write.
    // =====================================================================

    [Fact]
    public async Task BatchWrite_ReturnsPerSymbolSuccess()
    {
        await using var h = await CreateHarnessAsync();

        var results = await h.Connection.WriteValuesAsync(
            new Dictionary<string, object?> { ["MAIN.a"] = 1, ["MAIN.b"] = 2 },
            CancellationToken.None);

        Assert.True(results["MAIN.a"].Succeeded);
        Assert.True(results["MAIN.b"].Succeeded);
        Assert.Equal(1, await h.Connection.ReadValueAsync("MAIN.a", CancellationToken.None));
        Assert.Equal(2, await h.Connection.ReadValueAsync("MAIN.b", CancellationToken.None));
    }

    [Fact]
    public async Task BatchWrite_NullValue_YieldsPerSymbolFailure_ArgumentNull()
    {
        await using var h = await CreateHarnessAsync();

        var results = await h.Connection.WriteValuesAsync(
            new Dictionary<string, object?> { ["MAIN.ok"] = 1, ["MAIN.bad"] = null },
            CancellationToken.None);

        Assert.True(results["MAIN.ok"].Succeeded);
        Assert.False(results["MAIN.bad"].Succeeded);
        Assert.IsType<ArgumentNullException>(results["MAIN.bad"].Error);
        // The null was rejected and NOT stored.
        Assert.Null(await h.Connection.ReadValueAsync("MAIN.bad", CancellationToken.None));
    }

    // =====================================================================
    // Subscriptions.
    // =====================================================================

    [Fact]
    public async Task Subscribe_Untyped_FiresOnChangedWrite()
    {
        await using var h = await CreateHarnessAsync();

        var received = new TaskCompletionSource<(string, object?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = await h.Connection.SubscribeAsync(
            "MAIN.x", 100, (p, v) => received.TrySetResult((p, v)), CancellationToken.None);

        await h.WriteRawAsync("MAIN.x", 99);

        var (path, value) = await received.Task.WaitAsync(Timeout);
        Assert.Equal("MAIN.x", path);
        Assert.Equal(99, value);
    }

    [Fact]
    public async Task Subscribe_Typed_FiresOnChangedWrite_Converted()
    {
        await using var h = await CreateHarnessAsync();

        var received = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = await h.Connection.SubscribeAsync<double>(
            "MAIN.x", 100, (_, v) => received.TrySetResult(v), CancellationToken.None);

        await h.WriteRawAsync("MAIN.x", 7); // boxed int → widened to double

        var value = await received.Task.WaitAsync(Timeout);
        Assert.Equal(7.0, value);
    }

    [Fact]
    public async Task Subscribe_SameValueWrite_DoesNotFire()
    {
        await using var h = await CreateHarnessAsync();
        await h.WriteRawAsync("MAIN.x", 5); // seed before subscribing

        var count = 0;
        using var sub = await h.Connection.SubscribeAsync(
            "MAIN.x", 100, (_, _) => Interlocked.Increment(ref count), CancellationToken.None);

        await h.WriteRawAsync("MAIN.x", 5); // same value → no fire

        await Task.Delay(SettleDelay);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Subscribe_Dispose_StopsDelivery()
    {
        await using var h = await CreateHarnessAsync();

        var count = 0;
        var sub = await h.Connection.SubscribeAsync(
            "MAIN.x", 100, (_, _) => Interlocked.Increment(ref count), CancellationToken.None);

        await h.WriteRawAsync("MAIN.x", 1);
        await WaitUntil(() => Volatile.Read(ref count) == 1);

        sub.Dispose();

        await h.WriteRawAsync("MAIN.x", 2);
        await Task.Delay(SettleDelay);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task Subscribe_Dispose_IsIdempotent()
    {
        await using var h = await CreateHarnessAsync();

        var sub = await h.Connection.SubscribeAsync(
            "MAIN.x", 100, (_, _) => { }, CancellationToken.None);

        sub.Dispose();
        var ex = Record.Exception(() => sub.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public async Task Subscribe_TwoSubscribers_AreIndependent()
    {
        await using var h = await CreateHarnessAsync();

        var a = 0;
        var b = 0;
        var subA = await h.Connection.SubscribeAsync(
            "MAIN.x", 100, (_, _) => Interlocked.Increment(ref a), CancellationToken.None);
        using var subB = await h.Connection.SubscribeAsync(
            "MAIN.x", 100, (_, _) => Interlocked.Increment(ref b), CancellationToken.None);

        await h.WriteRawAsync("MAIN.x", 1);
        await WaitUntil(() => Volatile.Read(ref a) == 1 && Volatile.Read(ref b) == 1);

        // Dispose A; B keeps receiving.
        subA.Dispose();
        await h.WriteRawAsync("MAIN.x", 2);
        await WaitUntil(() => Volatile.Read(ref b) == 2);

        Assert.Equal(1, Volatile.Read(ref a));
        Assert.Equal(2, Volatile.Read(ref b));
    }

    [Fact]
    public async Task Subscribe_Typed_MismatchedNotification_Dropped_OthersUnaffected()
    {
        await using var h = await CreateHarnessAsync();

        var typedHits = 0;
        var untypedHits = 0;
        using var typed = await h.Connection.SubscribeAsync<int>(
            "MAIN.x", 100, (_, _) => Interlocked.Increment(ref typedHits), CancellationToken.None);
        using var untyped = await h.Connection.SubscribeAsync(
            "MAIN.x", 100, (_, _) => Interlocked.Increment(ref untypedHits), CancellationToken.None);

        // A value the int subscriber cannot convert: the typed notification is dropped,
        // the untyped one still fires.
        await h.WriteRawAsync("MAIN.x", "not-an-int");

        await WaitUntil(() => Volatile.Read(ref untypedHits) == 1);
        await Task.Delay(SettleDelay);

        Assert.Equal(0, Volatile.Read(ref typedHits));
        Assert.Equal(1, Volatile.Read(ref untypedHits));
    }

    // =====================================================================
    // State surface.
    // =====================================================================

    [Fact]
    public async Task State_IsReadable()
    {
        await using var h = await CreateHarnessAsync();
        // A connected harness reads Connected; the assertion only pins that the property is
        // readable and returns a defined enum value.
        Assert.True(Enum.IsDefined(h.Connection.State));
    }

    [Fact]
    public async Task IsConnected_IsReadable()
    {
        await using var h = await CreateHarnessAsync();
        var connected = h.Connection.IsConnected;
        Assert.True(connected);
    }

    // =====================================================================
    // Cancellation: pre-cancelled token throws on every operation.
    // =====================================================================

    [Fact]
    public async Task PreCancelledToken_Read_Throws()
    {
        await using var h = await CreateHarnessAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Connection.ReadValueAsync("MAIN.x", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Connection.ReadValueAsync<int>("MAIN.x", cts.Token));
    }

    [Fact]
    public async Task PreCancelledToken_Write_Throws()
    {
        await using var h = await CreateHarnessAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Connection.WriteValueAsync("MAIN.x", 1, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Connection.WriteValueAsync<int>("MAIN.x", 1, cts.Token));
    }

    [Fact]
    public async Task PreCancelledToken_Batch_Throws()
    {
        await using var h = await CreateHarnessAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Connection.ReadValuesAsync(["MAIN.x"], cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Connection.WriteValuesAsync(
                new Dictionary<string, object?> { ["MAIN.x"] = 1 }, cts.Token));
    }

    [Fact]
    public async Task PreCancelledToken_Subscribe_Throws()
    {
        await using var h = await CreateHarnessAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Connection.SubscribeAsync("MAIN.x", 100, (_, _) => { }, cts.Token));
    }

    // =====================================================================
    // Shared timing knobs (real-time guards only; never a primary sync mechanism).
    // =====================================================================

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(50);

    private static async Task WaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Predicate did not become true within the real-time guard window.");
            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }
    }
}
