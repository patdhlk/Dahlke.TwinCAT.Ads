namespace Dahlke.TwinCAT.Ads.Testing;

/// <summary>
/// A started, simulated PLC fleet for tests — a pool with seeded targets, ready to inject
/// into a system under test, with no generic-host boilerplate.
/// </summary>
/// <remarks>
/// <para>
/// Depends on no test framework, so it works from xunit, NUnit, MSTest or a plain console
/// harness alike.
/// </para>
/// <example>
/// <code>
/// await using var plc = await TestPlc.Create()
///     .WithTarget("plc1", seed => seed["GVL.Temp"] = 21.5f)
///     .StartAsync();
///
/// var sut = new TempService(plc.Pool);
/// </code>
/// </example>
/// </remarks>
public sealed class TestPlc : IAsyncDisposable
{
    private readonly AdsConnectionPoolHandle _handle;
    private readonly Dictionary<string, TestPlcTarget> _targets =
        new(StringComparer.OrdinalIgnoreCase);

    internal TestPlc(AdsConnectionPoolHandle handle, IEnumerable<string> targetIds)
    {
        _handle = handle;

        try
        {
            // Built eagerly, at start, so every target is recording from the first
            // instruction of the test. A lazily-created recorder would miss writes the
            // system under test made before the test first asked for the handle.
            foreach (var plcId in targetIds)
            {
                if (!handle.TryGetSimulatedConnection(plcId, out var simulated))
                    throw new InvalidOperationException(
                        $"Target '{plcId}' did not start as a simulated connection. This should be "
                        + "impossible for a TestPlc, which forces simulation for every target.");

                _targets[plcId] = new TestPlcTarget(plcId, simulated);
            }
        }
        catch
        {
            // A throw mid-loop must not leave the targets built so far still subscribed
            // and the pool still running: the constructor never returned, so nothing
            // will ever call DisposeAsync to clean them up. AsTask().GetAwaiter()
            // .GetResult() is the pattern AdsConnectionPoolHandle.DisposeAsync's own
            // remarks name for exactly this — a synchronous context with no way to await.
            foreach (var target in _targets.Values)
                target.Dispose();

            handle.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>Starts configuring a harness.</summary>
    public static TestPlcBuilder Create() => new();

    /// <summary>
    /// The pool to hand to the system under test, exactly as it would receive one from DI.
    /// </summary>
    public IAdsConnectionPool Pool => _handle;

    /// <summary>The raw ADS channel factory, for code under test that uses raw channels.</summary>
    public IAdsRawChannelFactory RawChannels => _handle.RawChannels;

    /// <summary>
    /// The stable connection facade for a target — the same instance
    /// <c>Pool.GetConnection(plcId)</c> returns.
    /// </summary>
    /// <param name="plcId">The target identifier, matched case-insensitively.</param>
    /// <exception cref="UnknownPlcTargetException">No such target is configured.</exception>
    public IAdsConnection Connection(string plcId) => _handle.GetConnection(plcId);

    /// <summary>
    /// The driver handle for a target: seed it, drive it, and read back what the code
    /// under test wrote.
    /// </summary>
    /// <param name="plcId">The target identifier, matched case-insensitively.</param>
    /// <returns>
    /// The handle for that target. Its identity is stable for the harness's lifetime, so
    /// the write log accumulates across calls.
    /// </returns>
    /// <exception cref="UnknownPlcTargetException">No such target is configured.</exception>
    public TestPlcTarget Target(string plcId)
    {
        ArgumentNullException.ThrowIfNull(plcId);

        if (_targets.TryGetValue(plcId, out var target))
            return target;

        throw new UnknownPlcTargetException(
            plcId, _targets.Keys.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Stops the pool and detaches every target recorder. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var target in _targets.Values)
            target.Dispose();

        await _handle.DisposeAsync().ConfigureAwait(false);
    }
}
