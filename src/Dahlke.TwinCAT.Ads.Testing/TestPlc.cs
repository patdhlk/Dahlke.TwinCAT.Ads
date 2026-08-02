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

    internal TestPlc(AdsConnectionPoolHandle handle) => _handle = handle;

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

    /// <summary>Stops the pool. Idempotent.</summary>
    public ValueTask DisposeAsync() => _handle.DisposeAsync();
}
