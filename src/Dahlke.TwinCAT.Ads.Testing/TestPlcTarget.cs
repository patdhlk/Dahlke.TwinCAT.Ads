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
    // a reactive write recorded on plc2. Each instance's buffer is independent.
    private readonly AsyncLocal<List<PlcWrite>?> _harnessWriteBuffer = new();

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
    public IReadOnlyList<PlcWrite> Writes
    {
        get { lock (_gate) return _writes.ToArray(); }
    }

    /// <summary>
    /// The writes the code under test made to one symbol path, oldest first. The path is
    /// matched case-insensitively, as every simulated symbol path is.
    /// </summary>
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

        var buffer = new List<PlcWrite>();
        _harnessWriteBuffer.Value = buffer;
        try
        {
            // The simulated write completes synchronously; awaiting it would flow the
            // AsyncLocal through a continuation for no benefit. Every ValueWritten this
            // call causes — the code under test's reactions first, this write's own
            // last — lands in `buffer` via OnValueWritten below.
            _simulated.WriteValueAsync(symbolPath, value).GetAwaiter().GetResult();
        }
        finally
        {
            _harnessWriteBuffer.Value = null;

            // The harness's own ValueWritten is guaranteed to be the buffer's LAST
            // entry — see the field comment on _harnessWriteBuffer for why. Commit
            // everything except that last entry; an unexpectedly empty buffer commits
            // nothing rather than throwing. Empty should be impossible (WriteValueAsync
            // always raises ValueWritten for its own write, changed or not), but a
            // defensive no-op costs nothing next to an IndexOutOfRangeException off a
            // write that already happened.
            if (buffer.Count > 0)
            {
                lock (_gate)
                    _writes.AddRange(buffer.GetRange(0, buffer.Count - 1));
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

        // A buffer installed on THIS instance's AsyncLocal means this event fired
        // inside THIS target's own Write call — either the harness's own write or a
        // reaction it triggered. Buffer it; Write sorts out which is which once the
        // call completes. No buffer means this write reached the store some other way
        // (the code under test, or a reaction driven on a different target's Write),
        // so it goes straight to the log.
        var buffer = _harnessWriteBuffer.Value;
        if (buffer is not null)
        {
            buffer.Add(write);
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
