using System.Globalization;
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
public sealed class TestPlcTarget
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
    // discard-last rule depends on.
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
    //
    // The thread-ID check alone is still not enough: it asks "same thread?" but never
    // "is this scope still live?". AsyncLocal keeps a scope reachable from ANY
    // continuation whose ExecutionContext was captured while that scope was installed —
    // and the thread pool can later resume such a continuation on the very thread that
    // installed the scope, after that scope's OWN Write call already returned and
    // drained it. A thread-only check matches that stale scope and appends into a
    // buffer nobody will ever read again — the same orphaning the thread check was
    // meant to prevent, reached through a different door. HarnessWriteScope.Closed
    // closes that gap: set as the very first action in Write's finally (before the
    // AsyncLocal is even restored), and required — alongside the thread-ID check, not
    // instead of it — everywhere a scope's buffer might be touched.
    private readonly AsyncLocal<HarnessWriteScope?> _harnessWrite = new();

    // One per in-flight (possibly nested) Write call on one thread.
    private sealed class HarnessWriteScope
    {
        internal HarnessWriteScope(int threadId) => ThreadId = threadId;
        internal int ThreadId { get; }
        internal List<PlcWrite> Buffered { get; } = [];

        // Set as the first action in Write's finally — see the field comment on
        // _harnessWrite. A plain bool, not volatile and not Interlocked, which is
        // deliberate. Buffered is touched from exactly three places. Two of them belong
        // to a call other than the one that installed the scope — OnValueWritten's
        // append, and the nested-commit AddRange in Write below — and BOTH require this
        // flag and ThreadId to be checked together, never ThreadId alone. The third is
        // the installing Write call's own finally, which reads Buffered.Count and calls
        // GetRange with no guard at all; it needs none, precisely because the two guarded
        // paths insist on a ThreadId match, making the installing thread the only thread
        // that can ever append to a scope's Buffered — so that read is that same thread
        // reading back its own list, synchronously, with no other writer possible. For
        // the two guarded paths, a matching ThreadId with Closed still false can only
        // mean the literal same physical thread reading its own scope back — always safe,
        // a thread's own prior writes are visible to its own later reads in program order
        // without a barrier. The only way a DIFFERENT physical thread could ever see a
        // matching ThreadId is the runtime reusing a terminated thread's numeric
        // ManagedThreadId, which requires that original thread to have already exited —
        // and a thread cannot exit mid-Write, so by the time its ID is up for reuse,
        // Closed is already true and thread termination itself is a safe publication
        // point for that write. Every mismatched-thread case routes to `_writes` directly
        // regardless of what `Closed` reads as, so a hypothetically stale read here can
        // never change the outcome.
        internal bool Closed { get; set; }
    }

    private readonly List<PlcWrite> _writes = [];
    private readonly object _gate = new();
    private readonly SimulatedAdsConnection _simulated;
    private bool _detached;

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
            // Closed FIRST, before the AsyncLocal is even restored — see
            // HarnessWriteScope.Closed for why the order matters and why a plain bool
            // is safe here without volatile or Interlocked.
            scope.Closed = true;
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

                // Nested inside an outer Write, on the same thread, and that outer
                // call's own finally has not yet run: hand the remainder up into the
                // outer scope rather than `_writes` directly — it is still the outer
                // call's dynamic extent, and the outer call's own finally will
                // discard-last again once IT completes. Both conjuncts are required: a
                // Task.Run spawned from a callback can flow `previous` in from an
                // ALREADY-closed outer scope (previous.Closed) or from a DIFFERENT
                // thread than the one that installed it (previous.ThreadId) — either
                // one means the outer scope's own buffer is not a safe place to write
                // into, and this write belongs in `_writes` directly instead, same as
                // any other cross-thread write.
                if (previous is not null && !previous.Closed
                    && previous.ThreadId == Environment.CurrentManagedThreadId)
                {
                    previous.Buffered.AddRange(reactive);
                }
                else
                {
                    lock (_gate)
                        _writes.AddRange(reactive);
                }
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

    /// <summary>
    /// Asserts the code under test wrote to <paramref name="symbolPath"/> at least once,
    /// with any value.
    /// </summary>
    /// <param name="symbolPath">The path that should have been written.</param>
    /// <exception cref="PlcAssertionException">No write to that path was recorded.</exception>
    public void AssertWritten(string symbolPath)
    {
        ArgumentNullException.ThrowIfNull(symbolPath);

        var writes = WritesTo(symbolPath);
        if (writes.Count > 0)
            return;

        throw new PlcAssertionException(
            $"Expected a write to \"{symbolPath}\" on {PlcId}, but {DescribeRecorded(writes)}");
    }

    /// <summary>
    /// Asserts the code under test wrote <paramref name="expected"/> to
    /// <paramref name="symbolPath"/> at least once.
    /// </summary>
    /// <param name="symbolPath">The path that should have been written.</param>
    /// <param name="expected">
    /// The value expected. Compared with <see cref="object.Equals(object, object)"/>, which
    /// for boxed values is TYPE-SENSITIVE: a boxed <see cref="float"/> 23.5 does not equal
    /// a boxed <see cref="double"/> 23.5. That is the same rule the simulated connection
    /// uses to decide whether a write is a change, so the two agree — and it is why a
    /// failure message names the CLR type of everything it prints.
    /// </param>
    /// <exception cref="PlcAssertionException">No matching write was recorded.</exception>
    public void AssertWritten(string symbolPath, object? expected)
    {
        ArgumentNullException.ThrowIfNull(symbolPath);

        var writes = WritesTo(symbolPath);
        if (writes.Any(w => Equals(w.Value, expected)))
            return;

        throw new PlcAssertionException(
            $"Expected a write of {Describe(expected)} to \"{symbolPath}\" on {PlcId}, "
            + $"but {DescribeRecorded(writes)}");
    }

    /// <summary>
    /// Asserts the code under test never wrote to <paramref name="symbolPath"/>.
    /// </summary>
    /// <param name="symbolPath">The path that should not have been written.</param>
    /// <exception cref="PlcAssertionException">At least one write was recorded.</exception>
    public void AssertNotWritten(string symbolPath)
    {
        ArgumentNullException.ThrowIfNull(symbolPath);

        var writes = WritesTo(symbolPath);
        if (writes.Count == 0)
            return;

        throw new PlcAssertionException(
            $"Expected no write to \"{symbolPath}\" on {PlcId}, but {DescribeRecorded(writes)}");
    }

    /// <summary>
    /// Asserts the code under test wrote to <paramref name="symbolPath"/> exactly
    /// <paramref name="expected"/> times.
    /// </summary>
    /// <param name="symbolPath">The path to count writes for.</param>
    /// <param name="expected">The exact number of writes expected.</param>
    /// <exception cref="PlcAssertionException">A different number was recorded.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expected"/> is negative.</exception>
    public void AssertWriteCount(string symbolPath, int expected)
    {
        ArgumentNullException.ThrowIfNull(symbolPath);
        ArgumentOutOfRangeException.ThrowIfNegative(expected);

        var writes = WritesTo(symbolPath);
        if (writes.Count == expected)
            return;

        throw new PlcAssertionException(
            $"Expected exactly {expected} write(s) to \"{symbolPath}\" on {PlcId}, "
            + $"but {DescribeRecorded(writes)}");
    }

    /// <summary>
    /// Renders a value with its CLR type. Without the type, a Single/Double mismatch prints
    /// as "expected 23.5, got 23.5" — which is the most confusing possible message for the
    /// most common possible cause. Formatted with <see cref="CultureInfo.InvariantCulture"/>
    /// rather than the current culture, so e.g. 23.5 always renders with a period rather than
    /// a comma on a host whose locale uses one — the message is for a developer reading test
    /// output, not for end-user display.
    /// </summary>
    private static string Describe(object? value) =>
        value is null
            ? "null"
            : $"{string.Format(CultureInfo.InvariantCulture, "{0}", value)} ({value.GetType().Name})";

    /// <summary>
    /// Renders the recorded writes for a path, for the tail of every failure message.
    /// Harness writes are excluded from the log, so this reports only what the code under
    /// test did — which is exactly what the assertion is about.
    /// </summary>
    /// <param name="writes">
    /// The very snapshot the caller decided to fail on, NOT a fresh <see cref="WritesTo"/>
    /// query. Re-querying would take an independent snapshot: under a concurrently mutating
    /// log, <see cref="AssertNotWritten"/> could fail BECAUSE writes exist and then print
    /// "no writes to that path were recorded" — a message that does not merely go stale, it
    /// names the wrong cause and sends the reader hunting the harness-writes-are-excluded
    /// rule for a failure that has nothing to do with it.
    /// </param>
    private static string DescribeRecorded(IReadOnlyList<PlcWrite> writes)
    {
        if (writes.Count == 0)
            return "no writes to that path were recorded. Note that writes made through "
                + "TestPlcTarget.Write drive the PLC side and are deliberately not recorded — "
                + "only writes made by the code under test are.";

        var lines = writes.Select((w, i) => $"{Environment.NewLine}  [{i}] {Describe(w.Value)}");
        return $"{writes.Count} write(s) were recorded:{string.Concat(lines)}";
    }

    private void OnValueWritten(object? sender, SimulatedWriteEventArgs e)
    {
        var write = new PlcWrite(e.SymbolPath, e.Value, e.PreviousValue, e.Changed);

        // A scope installed on THIS instance's AsyncLocal, NOT YET Closed, AND whose
        // installing thread is the one raising this event, means the event fired
        // inside THIS target's own Write call, synchronously, on the thread the
        // discard-last guarantee actually covers — either the harness's own write or a
        // reaction it triggered. Buffer it; Write sorts out which is which once the
        // call completes.
        //
        // A scope with a DIFFERENT ThreadId means the AsyncLocal flowed here from a
        // Task.Run spawned inside a callback — a genuinely different thread, outside
        // the guarantee the buffer depends on. A scope that IS Closed means the
        // AsyncLocal flowed here from a continuation whose context was captured while
        // the scope was live but is resuming — possibly on the very thread that
        // installed it — after that scope's own Write call already drained and
        // abandoned it; ThreadId alone cannot tell that case apart from a still-live
        // scope, which is exactly why Closed exists. Either way this write goes
        // straight to the log, the same as one with no scope at all (the code under
        // test, or a reaction driven on a different target's Write).
        var scope = _harnessWrite.Value;
        if (scope is { Closed: false } && scope.ThreadId == Environment.CurrentManagedThreadId)
        {
            scope.Buffered.Add(write);
            return;
        }

        lock (_gate)
            _writes.Add(write);
    }

    // Detaches from the simulated connection. Idempotent. Called by TestPlc.DisposeAsync
    // and by TestPlc's constructor unwind path — never by a test.
    //
    // Deliberately internal, and deliberately NOT an IDisposable implementation. A target
    // is cached: TestPlc.Target(plcId) hands back the same instance every time, for the
    // harness's whole lifetime. So a consumer who wrote `using (var t = plc.Target("plc1"))`
    // — the shape an IDisposable invites, and the one CA2000 actively nudges toward —
    // would detach the recorder permanently while Write, Seed and Read all kept working.
    // Every subsequent assertion would then run against an empty log and PASS:
    // AssertNotWritten and AssertWriteCount(path, 0) would confirm a write that really
    // landed never happened. That silent false pass is precisely the failure this type
    // exists to prevent, so the operation that causes it is not on the public surface at
    // all. Its absence from PublicAPI.Unshipped.txt is the guard — re-adding a public
    // Dispose trips RS0016.
    internal void Detach()
    {
        if (_detached)
            return;

        _detached = true;
        _simulated.ValueWritten -= OnValueWritten;
    }
}
