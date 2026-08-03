using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// A base class for hand-written <see cref="IAdsConnection"/> doubles: every operation throws
/// <see cref="NotSupportedException"/> until it is overridden, so a double declares only the
/// members the code under test actually reaches.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see cref="IAdsConnection"/> has over two dozen members. A consumer
/// faking only reads — the common case in a unit test for their own service — otherwise has to
/// implement all of them, or reach for a mocking framework to fill in the rest as throwing stubs.
/// Deriving from this class collapses that to the two or three that matter — here, a working
/// simulated connection that times out on the third read and nowhere else:
/// <code>
/// private sealed class FlakyConnection(IAdsConnection inner) : AdsConnectionBase
/// {
///     private int _reads;
///
///     public override string PlcId =&gt; inner.PlcId;
///
///     public override Task&lt;T&gt; ReadValueAsync&lt;T&gt;(string symbolPath, CancellationToken ct = default)
///         =&gt; Interlocked.Increment(ref _reads) == 3
///             ? throw new AdsErrorException("no answer", AdsErrorCode.ClientSyncTimeOut)
///             : inner.ReadValueAsync&lt;T&gt;(symbolPath, ct);
/// }
/// </code>
/// </para>
/// <para>
/// <b>Reach for <see cref="SimulatedAdsConnection"/> first.</b> It is a working connection with a
/// real value store, real subscriptions and real RPC seeding, and it covers most of what a test
/// needs. This class is for the case it deliberately does not cover: a connection that FAILS in a
/// specific way — a particular <see cref="AdsErrorCode"/>, a timeout on the third call, a symbol
/// that disappears mid-run — which is where hand-written doubles come from.
/// </para>
/// <para>
/// <b>Every operation throws; nothing returns a plausible value.</b> A default that answered a
/// read with <see langword="null"/>, or a browse with an empty list, would let a test pass while
/// exercising a code path the double was never told about. The exception names the deriving type
/// and the member, so a test that reaches an unimplemented one says exactly which override is
/// missing rather than failing somewhere downstream on a value it should never have had.
/// </para>
/// <para>
/// <b>Three members have working defaults</b>, because throwing there costs a double overrides
/// that have nothing to do with what it is testing:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="State"/>, <see cref="IsConnected"/> and <see cref="ConnectionStateChanged"/> are
///     a coherent trio: the connection starts <see cref="ConnectionState.Connected"/>, and
///     <see cref="SetConnectionState"/> moves it and raises the event in one call, so a double can
///     simulate an outage without re-deriving the bookkeeping every hand-written double gets
///     slightly differently.
///   </description></item>
///   <item><description>
///     <see cref="WithTimeout"/> validates its argument and returns <see langword="this"/>. A
///     double performs no I/O, so it has no bound to change and a scope over it is itself. Override
///     it when the test is ABOUT scoping — to record the requested bound, say.
///   </description></item>
/// </list>
/// <para>
/// <b><see cref="PlcId"/> and <see cref="DisplayName"/> still throw.</b> There is no honest default
/// for an identity: a double that reports the wrong one is exactly how a routing or per-target
/// logging test passes while testing nothing. Both are a one-line override.
/// </para>
/// <para>
/// <b>Thread safety.</b> The state trio is safe for concurrent use — a transition is applied with
/// an interlocked exchange, so two threads racing to change state each raise the event exactly
/// once for the transition they won. Everything a derived double adds is that double's own
/// responsibility.
/// </para>
/// <para>
/// <b>Adding members to <see cref="IAdsConnection"/> is still a breaking change for implementers.</b>
/// This class softens it — a double deriving from it keeps compiling and gets a throwing default
/// for the new member — but a double that implements the interface directly does not, and this
/// class existing is not a licence to grow the interface freely.
/// </para>
/// </remarks>
public abstract class AdsConnectionBase : IAdsConnection
{
    // int rather than a volatile ConnectionState field so a transition can be applied with
    // Interlocked.Exchange: the exchange both stores the new state and reports the previous one,
    // which is what makes "raise exactly once per transition" hold under concurrent callers.
    private int _state = (int)ConnectionState.Connected;

    // ---- Identity: no honest default, so both throw ------------------------

    /// <inheritdoc />
    /// <remarks>
    /// Throws until overridden. A double that reports an identity it was not given is how a
    /// routing test passes without routing anything, so there is no default here — and
    /// <see cref="SetConnectionState"/> reads this to build the event args, so a double that
    /// simulates an outage must override it.
    /// </remarks>
    public virtual string PlcId => throw NotSupported(nameof(PlcId));

    /// <inheritdoc />
    /// <remarks>Throws until overridden, for the same reason as <see cref="PlcId"/>.</remarks>
    public virtual string DisplayName => throw NotSupported(nameof(DisplayName));

    // ---- State: a working trio, moved by SetConnectionState ----------------

    /// <inheritdoc />
    /// <remarks>
    /// Starts at <see cref="ConnectionState.Connected"/> — a double that answers calls is
    /// connected — and follows <see cref="SetConnectionState"/> thereafter. Override this only
    /// when the double sources its state from somewhere else entirely; an override makes
    /// <see cref="SetConnectionState"/> and this property disagree, since the transition it
    /// records is no longer the one this reports.
    /// </remarks>
    public virtual ConnectionState State => (ConnectionState)Volatile.Read(ref _state);

    /// <inheritdoc />
    /// <remarks>
    /// Derived from <see cref="State"/>, including when <see cref="State"/> is overridden, so the
    /// two can never disagree.
    /// </remarks>
    public virtual bool IsConnected => State == ConnectionState.Connected;

    /// <inheritdoc />
    /// <remarks>
    /// Raised by <see cref="SetConnectionState"/> and by nothing else — a double that never calls
    /// it simply never fires, which is accurate rather than a lie: nothing has changed. Subscribing
    /// is always harmless.
    /// </remarks>
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// Moves this connection to <paramref name="state"/> and raises
    /// <see cref="ConnectionStateChanged"/> if that is a change, so a double can simulate an
    /// outage — or a recovery — in one call.
    /// </summary>
    /// <param name="state">The state to move to.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="state"/> is not a defined <see cref="ConnectionState"/> value.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="PlcId"/> has not been overridden and a handler needs it. In the common case this
    /// throws BEFORE the state moves: a handler was already attached when the call started, the
    /// event args need to name the target, and a double with no subscribers at that point never
    /// needs an identity to move its own state. If a handler instead subscribes CONCURRENTLY — in
    /// the window between that first check and the exchange below — <see cref="PlcId"/> is read
    /// again afterwards to tell it, and this can then throw with the state already moved; that
    /// ordering is unavoidable and only happens under exactly that race.
    /// </exception>
    /// <exception cref="AggregateException">
    /// One or more handlers threw. Every handler still ran (see remarks).
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Only a real transition raises.</b> Setting the state it already holds stores nothing new
    /// and fires nothing, matching the pool's own change-guard — a test asserting "one Disconnected
    /// event" is not defeated by a double that sets the state twice.
    /// </para>
    /// <para>
    /// <b>The new state is visible before the event.</b> A handler that reads <see cref="State"/>
    /// or <see cref="IsConnected"/> sees the value the args carry, not the previous one.
    /// </para>
    /// <para>
    /// <b>A throwing handler is surfaced, not swallowed — the one place this deliberately differs
    /// from a real connection.</b> The live pool logs a faulty handler at Warning and carries on,
    /// because one bad subscriber must not stop reconnection; doing that here would turn a broken
    /// handler into a silently passing test. Every handler is still invoked — one that throws does
    /// not skip the rest, exactly as in the pool — and the failures are then rethrown together as
    /// an <see cref="AggregateException"/> at this call site.
    /// </para>
    /// <para>
    /// <b><see cref="PlcId"/> is read only when there is a handler to tell — but subscribers are
    /// read on BOTH sides of the exchange, not just before it.</b> The pre-exchange read decides
    /// whether an identity is needed yet and, if a double never overrode <see cref="PlcId"/>, fails
    /// fast with the state still where it was. Subscribers are then read again after the exchange,
    /// because a handler can attach in the window between the two reads — and a caller using
    /// <see cref="AdsConnectionExtensions.WaitForConnectedAsync"/>'s subscribe-then-recheck pattern
    /// lands exactly there: it re-reads <see cref="IsConnected"/> immediately after subscribing, so
    /// if this method only ever consulted its first, pre-exchange snapshot, that caller's handler
    /// would never be told and it would wait out its full timeout on a connection that had already
    /// moved. A double moving its own state with nobody subscribed at all still needs no identity,
    /// on either read. Internally, <see cref="AdsConnectionFacade.OnStateChanged"/> has always
    /// committed its state before reading subscribers for the same reason; this method now agrees
    /// with it instead of racing against the same class of caller it does.
    /// </para>
    /// <para>
    /// <b>Concurrent callers.</b> The transition is applied with an interlocked exchange, so each
    /// change raises exactly once even when two threads race. The RAISES are not otherwise ordered
    /// against each other: two transitions in flight at once can deliver in either order, and a
    /// handler can be running for one while the other is applied. A double driving state from
    /// several threads should serialise the calls itself if its test depends on the order.
    /// </para>
    /// </remarks>
    protected void SetConnectionState(ConnectionState state)
    {
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Not a defined ConnectionState value.");

        // Read the subscribers before the exchange ONLY to decide whether identity is needed: a
        // double that never overrode PlcId still throws here, with its state still where it was,
        // rather than landing in the new state having told nobody it got there.
        var handlersBefore = ConnectionStateChanged;
        var plcId = handlersBefore is null ? null : PlcId;

        var previous = (ConnectionState)Interlocked.Exchange(ref _state, (int)state);
        if (previous == state)
            return;

        // Re-read AFTER the exchange: a handler that subscribed in the window between the read
        // above and this exchange would otherwise never be told, and that window is exactly where
        // a subscribe-then-recheck waiter — see AdsConnectionExtensions.WaitForConnectedAsync —
        // lands.
        var handlers = ConnectionStateChanged;
        if (handlers is null)
            return;

        // May not have been resolved above (no subscriber then, one now). If a handler subscribed
        // concurrently, this reads PlcId AFTER the state already moved — see the NotSupportedException
        // remarks on this method for why that is unavoidable.
        plcId ??= PlcId;

        // Invoke each handler individually so one throwing handler does not skip the rest — the
        // multicast delegate would abort the chain on the first exception. Same shape as the
        // facade's raise, differing only in what happens to the exceptions afterwards.
        var args = new ConnectionStateChangedEventArgs(plcId!, state, previous);
        List<Exception>? failures = null;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<ConnectionStateChangedEventArgs>)handler)(this, args);
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(
                $"{failures.Count} ConnectionStateChanged handler(s) threw while reporting " +
                $"{previous} -> {state}.",
                failures);
        }
    }

    // ---- Scoping: a double has no bound to change --------------------------

    /// <inheritdoc />
    /// <remarks>
    /// A double performs no I/O, so there is no bound for a scope to replace and a scope over it is
    /// itself. The argument is still validated, so a call site passing a nonsense bound fails here
    /// exactly as it would against a real connection. Override this when the test is ABOUT scoping
    /// — to record the requested bound, or to hand back a differently-behaved double.
    /// </remarks>
    public virtual IAdsConnection WithTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        return this;
    }

    // ---- Values ------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>Throws until overridden.</remarks>
    public virtual Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct = default)
        => throw NotSupported(nameof(ReadValueAsync));

    /// <inheritdoc />
    /// <remarks>Throws until overridden.</remarks>
    public virtual Task<object?> ReadValueAsync(string symbolPath, CancellationToken ct = default)
        => throw NotSupported(nameof(ReadValueAsync));

    /// <inheritdoc />
    /// <remarks>Throws until overridden.</remarks>
    public virtual Task<AdsValueResult> ReadValueWithMetadataAsync(string symbolPath, CancellationToken ct = default)
        => throw NotSupported(nameof(ReadValueWithMetadataAsync));

    /// <inheritdoc />
    /// <remarks>Throws until overridden.</remarks>
    public virtual Task WriteValueAsync<T>(string symbolPath, T value, CancellationToken ct = default)
        => throw NotSupported(nameof(WriteValueAsync));

    /// <inheritdoc />
    /// <remarks>Throws until overridden.</remarks>
    public virtual Task WriteValueAsync(string symbolPath, object value, CancellationToken ct = default)
        => throw NotSupported(nameof(WriteValueAsync));

    /// <inheritdoc />
    /// <remarks>Throws until overridden.</remarks>
    public virtual Task<IReadOnlyDictionary<string, AdsValueResult>> ReadValuesAsync(
        IEnumerable<string> symbolPaths, CancellationToken ct = default)
        => throw NotSupported(nameof(ReadValuesAsync));

    /// <inheritdoc />
    /// <remarks>Throws until overridden.</remarks>
    public virtual Task<IReadOnlyDictionary<string, AdsValueResult>> WriteValuesAsync(
        IReadOnlyDictionary<string, object?> values, CancellationToken ct = default)
        => throw NotSupported(nameof(WriteValuesAsync));

    // ---- Methods and type metadata ----------------------------------------

    /// <inheritdoc />
    /// <remarks>Throws until overridden.</remarks>
    public virtual Task<AdsRpcResult> InvokeRpcMethodAsync(
        string symbolPath, string methodName, object?[] parameters, CancellationToken ct = default)
        => throw NotSupported(nameof(InvokeRpcMethodAsync));

    /// <inheritdoc />
    /// <remarks>Throws until overridden.</remarks>
    public virtual Task<IReadOnlyList<AdsEnumMember>> GetEnumMembersAsync(
        string typeName, CancellationToken ct = default)
        => throw NotSupported(nameof(GetEnumMembersAsync));

    // ---- Device ------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// Throws until overridden. Note this is the DEVICE's <see cref="AdsState"/> — Run, Stop,
    /// Config — and is unrelated to <see cref="State"/>, which reports whether this connection
    /// exists at all. The device state has no default because a double that claims
    /// <see cref="AdsState.Run"/> is asserting something about a PLC it does not have.
    /// </remarks>
    public virtual Task<AdsState> GetAdsStateAsync(CancellationToken ct = default)
        => throw NotSupported(nameof(GetAdsStateAsync));

    /// <inheritdoc />
    /// <remarks>Throws until overridden.</remarks>
    public virtual Task<AdsDeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
        => throw NotSupported(nameof(GetDeviceInfoAsync));

    /// <inheritdoc />
    /// <remarks>Throws until overridden.</remarks>
    public virtual Task WriteControlAsync(AdsState state, ushort deviceState, CancellationToken ct = default)
        => throw NotSupported(nameof(WriteControlAsync));

    // ---- Subscriptions -----------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// Throws until overridden. An override returning a handle owns the callback: nothing here
    /// fires it, and nothing here notices whether the handle is ever disposed.
    /// </remarks>
    public virtual Task<IDisposable> SubscribeAsync(
        string symbolPath, int cycleTimeMs, Action<string, object?> callback, CancellationToken ct = default)
        => throw NotSupported(nameof(SubscribeAsync));

    /// <inheritdoc />
    /// <remarks>Throws until overridden.</remarks>
    public virtual Task<IDisposable> SubscribeAsync<T>(
        string symbolPath, int cycleTimeMs, Action<string, T?> callback, CancellationToken ct = default)
        => throw NotSupported(nameof(SubscribeAsync));

    /// <inheritdoc />
    /// <remarks>Throws until overridden.</remarks>
    public virtual Task<IDisposable> SubscribeAsync(
        string symbolPath, int cycleTimeMs, Action<AdsNotification> callback, CancellationToken ct = default)
        => throw NotSupported(nameof(SubscribeAsync));

    // ---- Symbols -----------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>Throws until overridden.</remarks>
    public virtual Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolTreeAsync(
        string? parentPath, CancellationToken ct = default)
        => throw NotSupported(nameof(GetSymbolTreeAsync));

    /// <inheritdoc />
    /// <remarks>
    /// Throws until overridden. Deprecated on the interface and carried here only so a double
    /// deriving from this class satisfies <see cref="IAdsConnection"/>; a double should not need
    /// to override it, because the code under test should not be calling it.
    /// </remarks>
    [Obsolete("Use GetSymbolTreeAsync for the same behaviour, or GetSymbolsAsync(parentPath, includeChildren: false, ct) for one level.")]
    public virtual Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(
        string? parentPath, CancellationToken ct = default)
        => throw NotSupported(nameof(GetSymbolsAsync));

    /// <inheritdoc />
    /// <remarks>Throws until overridden.</remarks>
    public virtual Task<IReadOnlyList<AdsSymbolInfo>> GetSymbolsAsync(
        string? parentPath, bool includeChildren, CancellationToken ct = default)
        => throw NotSupported(nameof(GetSymbolsAsync));

    /// <inheritdoc />
    /// <remarks>Throws until overridden.</remarks>
    public virtual Task<IReadOnlyList<AdsSymbolInfo>> SearchSymbolsAsync(
        string pattern, bool includeChildren, CancellationToken ct = default)
        => throw NotSupported(nameof(SearchSymbolsAsync));

    // ---- The one message every unimplemented member produces ---------------

    // An instance method rather than a static one so the message can name the DERIVED type: a test
    // that trips this reads "FlakyConnection.ReadValuesAsync is not implemented", which is the
    // whole diagnostic — which double, which member. A static helper could only name this class,
    // which the reader already knows.
    private NotSupportedException NotSupported(string member) =>
        new($"{GetType().Name}.{member} is not implemented. {nameof(AdsConnectionBase)} throws for " +
            $"every member a double has not overridden, rather than returning a value the test " +
            $"never specified — override {member} if the code under test reaches it.");
}
