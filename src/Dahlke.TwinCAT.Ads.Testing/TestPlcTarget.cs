using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Testing;

/// <summary>
/// Drives one simulated target and records what the code under test wrote to it.
/// Obtain one from <see cref="TestPlc.Target"/>.
/// </summary>
/// <remarks>
/// <para>
/// Three verbs that are easy to conflate, kept distinct because the difference is
/// observable:
/// </para>
/// <list type="table">
///   <listheader><term>Verb</term><description>Fires subscriptions / recorded</description></listheader>
///   <item><term><see cref="Seed(string, object?)"/></term><description>no / no — fixture setup</description></item>
///   <item><term><see cref="Write"/></term><description>yes / <b>no</b> — drives the PLC side</description></item>
///   <item><term>a write by the code under test</term><description>yes / yes</description></item>
/// </list>
/// <para>
/// That <see cref="Write"/> is excluded from the log is the rule worth knowing. The log
/// answers "what did the code under test write", and a harness write is not that. Without
/// the exclusion, a test that primes <c>GVL.Setpoint</c> and then asserts the system under
/// test wrote it would pass while testing nothing.
/// </para>
/// </remarks>
public sealed class TestPlcTarget : IDisposable
{
    // Installed only for the duration of a harness Write call — NOT a boolean flag.
    // SimulatedAdsConnection fires subscription callbacks BEFORE ValueWritten,
    // synchronously, INSIDE the write that triggered them — see
    // SubscriptionCallbacksRunBeforeTheEvent in
    // tests/Dahlke.TwinCAT.Ads.Tests/SimulatedWriteEventTests.cs, which this design
    // depends on and must keep passing. So when the code under test reacts to a driven
    // input by writing an output, that reactive write's own ValueWritten also fires
    // while a boolean flag would still be set — wrongly suppressing exactly the SUT
    // write this log exists to catch. Buffering instead works because the same
    // before-the-event guarantee orders the buffer: every reactive write (however
    // deeply nested) is appended before the harness's own write's event fires, so the
    // harness's own is always the buffer's LAST entry — discard only that one.
    //
    // Per-instance, NOT static: a static AsyncLocal would share suppression state
    // across every TestPlcTarget in the process, including targets on unrelated
    // TestPlc harnesses running concurrently under a parallel test runner, and across
    // targets within the same harness — a write driven on plc1 would wrongly suppress
    // a reactive write recorded on plc2. Each instance's scope is independent.
    //
    // The scope also carries the ID of the thread that installed it, and that thread
    // check is not optional. The "harness's own write is always the buffer's LAST
    // entry" guarantee holds only for writes that happen synchronously, on the SAME
    // thread that called Write — that is the scope of
    // SubscriptionCallbacksRunBeforeTheEvent. AsyncLocal, however, also flows into a
    // Task.Run spawned from inside a callback, which runs on a DIFFERENT thread and is
    // outside that guarantee entirely: such a write might land after Write's own
    // finally has already read and cleared the scope (orphaning it — appended to a
    // list nobody will ever read again), or land on another thread while the scope is
    // still installed but interleaved with the owning thread's own appends (a List<T>
    // data race, and liable to invert the discard if it lands last). Checking the
    // thread ID routes both cases straight to `_writes` instead — correct, because a
    // write from any other thread was never part of the synchronous chain the
    // discard-last rule depends on — and removes the data race for free, since only
    // the installing thread is ever allowed to touch a scope's buffer.
    //
    // A prior version of this field cleared the AsyncLocal unconditionally in `finally`
    // rather than restoring the previous value, which broke a re-entrant Write (a
    // subscription callback that itself calls target.Write, e.g. "drive the next input
    // once the SUT acks"): the inner call's finally de-installed the OUTER scope, so
    // the outer write's own event found no scope and was wrongly recorded, while the
    // outer scope's real entries had nowhere to go. Save/restore fixes that: nesting on
    // the SAME thread composes correctly because each inner Write hands its own
    // (already discard-last'd) remainder up into the scope it displaced, rather than
    // into `_writes` directly.
    private readonly AsyncLocal<HarnessWriteScope?> _harnessWrite = new();

    // One per in-flight (possibly nested) Write call on one thread. Buffered is
    // touched ONLY by the thread recorded in ThreadId — see the field comment on
    // _harnessWrite — so it needs no lock of its own.
    private sealed class HarnessWriteScope
    {
        internal HarnessWriteScope(int threadId) => ThreadId = threadId;
        internal int ThreadId { get; }
        internal List<PlcWrite> Buffered { get; } = [];
    }

    private readonly List<PlcWrite> _writes = [];
    private readonly object _gate = new();
    private readonly SimulatedAdsConnection _simulated;
    private bool _disposed;

    internal TestPlcTarget(string plcId, SimulatedAdsConnection simulated)
    {
        PlcId = plcId;
        _simulated = simulated;
        _simulated.ValueWritten += OnValueWritten;
    }

    /// <summary>The configured identifier of this target.</summary>
    public string PlcId { get; }

    /// <summary>
    /// The live simulated connection, for anything this handle does not wrap — enum
    /// metadata, ADS state, or subscribing to every write including the harness's own.
    /// </summary>
    public SimulatedAdsConnection Simulated => _simulated;

    /// <summary>
    /// Every write the code under test made to this target, oldest first.
    /// </summary>
    /// <remarks>
    /// "Oldest first" means the order writes were recorded in, which is usually but not
    /// always the order they happened in. A write the code under test makes IN REACTION
    /// TO a <see cref="Write"/> call is recorded when that call returns, not the instant
    /// it happens — so it can appear after an unrelated write that another thread made
    /// directly to <c>_writes</c> while the <see cref="Write"/> call was still running.
    /// This is a narrow case (concurrent writers to the same target during a harness
    /// drive) and does not affect the far more common case of a single-threaded test.
    /// </remarks>
    public IReadOnlyList<PlcWrite> Writes
    {
        get { lock (_gate) return _writes.ToArray(); }
    }

    /// <summary>
    /// The writes the code under test made to one symbol path, oldest first. The path is
    /// matched case-insensitively, as every simulated symbol path is.
    /// </summary>
    /// <remarks>See the ordering note on <see cref="Writes"/>.</remarks>
    /// <param name="symbolPath">The path to filter on.</param>
    public IReadOnlyList<PlcWrite> WritesTo(string symbolPath)
    {
        ArgumentNullException.ThrowIfNull(symbolPath);

        lock (_gate)
            return _writes
                .Where(w => string.Equals(w.SymbolPath, symbolPath, StringComparison.OrdinalIgnoreCase))
                .ToArray();
    }

    /// <summary>Forgets every recorded write — useful between phases of a longer test.</summary>
    public void ClearWrites()
    {
        lock (_gate) _writes.Clear();
    }

    /// <summary>
    /// Seeds a value without firing subscriptions and without recording a write. Fixture
    /// setup: use it for state that should already exist when the test begins.
    /// </summary>
    /// <param name="symbolPath">The symbol path to seed.</param>
    /// <param name="value">The value, kept at its CLR type.</param>
    public void Seed(string symbolPath, object? value)
    {
        ArgumentNullException.ThrowIfNull(symbolPath);
        _simulated.SetInitialValues(new Dictionary<string, object?> { [symbolPath] = value });
    }

    /// <summary>
    /// Seeds several values at once, without firing subscriptions and without recording
    /// writes.
    /// </summary>
    /// <param name="values">The values to seed, keyed by symbol path.</param>
    public void Seed(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _simulated.SetInitialValues(values);
    }

    /// <summary>
    /// Drives the PLC side: writes a value and fires subscriptions, as a changing input
    /// would. <b>Not recorded</b> in <see cref="Writes"/> — see the type remarks.
    /// </summary>
    /// <param name="symbolPath">The symbol path to write.</param>
    /// <param name="value">The value, kept at its CLR type.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="symbolPath"/> or <paramref name="value"/> is null. A null value is
    /// rejected because a real connection cannot write one; use <see cref="Seed(string, object?)"/>
    /// if a null slot is genuinely what the test needs.
    /// </exception>
    public void Write(string symbolPath, object? value)
    {
        ArgumentNullException.ThrowIfNull(symbolPath);
        ArgumentNullException.ThrowIfNull(value);

        // Saved so a re-entrant Write (called from a subscription callback this very
        // call triggers, on the SAME thread) can hand its own remainder up into the
        // scope it is about to displace, and so `finally` can put that scope back
        // rather than leaving the AsyncLocal cleared out from under an outer call.
        var previous = _harnessWrite.Value;
        var scope = new HarnessWriteScope(Environment.CurrentManagedThreadId);
        _harnessWrite.Value = scope;
        try
        {
            // The simulated write completes synchronously; awaiting it would flow the
            // AsyncLocal through a continuation for no benefit. Every ValueWritten this
            // call causes on THIS thread — the code under test's reactions first, this
            // write's own last — lands in `scope.Buffered` via OnValueWritten below.
            _simulated.WriteValueAsync(symbolPath, value).GetAwaiter().GetResult();
        }
        finally
        {
            _harnessWrite.Value = previous;

            // The harness's own ValueWritten is guaranteed to be the scope's LAST
            // entry — see the field comment on _harnessWrite for why. Commit everything
            // except that last entry; an unexpectedly empty buffer commits nothing
            // rather than throwing. Empty should be impossible (WriteValueAsync always
            // raises ValueWritten for its own write, changed or not), but a defensive
            // no-op costs nothing next to an IndexOutOfRangeException off a write that
            // already happened.
            if (scope.Buffered.Count > 0)
            {
                var reactive = scope.Buffered.GetRange(0, scope.Buffered.Count - 1);

                // Nested inside an outer Write on the same thread: hand the remainder
                // up into the outer scope rather than `_writes` directly — it is still
                // the outer call's dynamic extent, and the outer call's own finally
                // will discard-last again once IT completes.
                if (previous is not null)
                    previous.Buffered.AddRange(reactive);
                else
                    lock (_gate)
                        _writes.AddRange(reactive);
            }
        }
    }

    /// <summary>Reads the current value of a symbol.</summary>
    /// <param name="symbolPath">The symbol path to read.</param>
    /// <returns>The stored value, or <see langword="null"/> if the symbol was never written.</returns>
    public object? Read(string symbolPath)
    {
        ArgumentNullException.ThrowIfNull(symbolPath);
        return _simulated.ReadValueAsync(symbolPath).GetAwaiter().GetResult();
    }

    /// <summary>Reads the current value of a symbol, converted to <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The type to convert to, by the simulated connection's own rules.</typeparam>
    /// <param name="symbolPath">The symbol path to read.</param>
    /// <returns>The stored value, converted to <typeparamref name="T"/>.</returns>
    /// <exception cref="AdsErrorException">
    /// The symbol was never written or seeded. Unlike <see cref="Read(string)"/>, which
    /// returns <see langword="null"/> for a missing symbol, this overload throws — a
    /// missing symbol has no value to convert to <typeparamref name="T"/>, and the
    /// caller explicitly asked for a concrete type. Matches
    /// <see cref="SimulatedAdsConnection.ReadValueAsync{T}(string, CancellationToken)"/>,
    /// which this wraps.
    /// </exception>
    public T Read<T>(string symbolPath)
    {
        ArgumentNullException.ThrowIfNull(symbolPath);
        return _simulated.ReadValueAsync<T>(symbolPath).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Seeds the result of a PLC method call. Seeding the same path and method again
    /// replaces the previous handler.
    /// </summary>
    /// <param name="symbolPath">The instance path the handler answers for.</param>
    /// <param name="methodName">The method name.</param>
    /// <param name="handler">Invoked with the caller's arguments.</param>
    public void SetRpc(string symbolPath, string methodName, Func<object?[], AdsRpcResult> handler)
        => _simulated.SetRpcHandler(symbolPath, methodName, handler);

    private void OnValueWritten(object? sender, SimulatedWriteEventArgs e)
    {
        var write = new PlcWrite(e.SymbolPath, e.Value, e.PreviousValue, e.Changed);

        // A scope installed on THIS instance's AsyncLocal, AND whose installing thread
        // is the one raising this event, means the event fired inside THIS target's own
        // Write call, synchronously, on the thread the discard-last guarantee actually
        // covers — either the harness's own write or a reaction it triggered. Buffer
        // it; Write sorts out which is which once the call completes.
        //
        // A scope with a DIFFERENT ThreadId means the AsyncLocal flowed here from a
        // Task.Run spawned inside a callback — a genuinely different thread, outside
        // the guarantee the buffer depends on. That write goes straight to the log, the
        // same as one with no scope at all (the code under test, or a reaction driven
        // on a different target's Write).
        var scope = _harnessWrite.Value;
        if (scope is not null && scope.ThreadId == Environment.CurrentManagedThreadId)
        {
            scope.Buffered.Add(write);
            return;
        }

        lock (_gate)
            _writes.Add(write);
    }

    /// <summary>
    /// Detaches from the simulated connection. Called by <see cref="TestPlc.DisposeAsync"/>;
    /// a test does not normally call it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _simulated.ValueWritten -= OnValueWritten;
    }
}
