namespace Dahlke.TwinCAT.Ads.Testing;

/// <summary>
/// Configures a <see cref="TestPlc"/> before starting it. Obtain one from
/// <see cref="TestPlc.Create"/>.
/// </summary>
public sealed class TestPlcBuilder
{
    private readonly AdsConnectionPoolBuilder _builder = AdsConnectionPoolBuilder.CreateSimulation();
    private readonly List<string> _targetIds = [];

    internal TestPlcBuilder() { }

    /// <summary>
    /// Adds an empty simulated target.
    /// </summary>
    /// <param name="plcId">The target identifier, matched case-insensitively.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="plcId"/> is null, empty or whitespace.</exception>
    public TestPlcBuilder WithTarget(string plcId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plcId);

        _builder.AddTarget(plcId, o =>
        {
            o.Mode = ConnectionMode.Simulated;
            if (string.IsNullOrEmpty(o.DisplayName))
                o.DisplayName = plcId;
        });

        if (!_targetIds.Contains(plcId, StringComparer.OrdinalIgnoreCase))
            _targetIds.Add(plcId);

        return this;
    }

    /// <summary>
    /// Adds a simulated target and seeds its initial values.
    /// </summary>
    /// <param name="plcId">The target identifier, matched case-insensitively.</param>
    /// <param name="seed">
    /// Populates the target's initial values, e.g. <c>seed =&gt; seed["GVL.Temp"] = 21.5f</c>.
    /// Values keep their CLR types and are seeded verbatim, so a metadata read reports the
    /// type a real PLC would.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="plcId"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="seed"/> is null.</exception>
    public TestPlcBuilder WithTarget(string plcId, Action<IDictionary<string, object?>> seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        WithTarget(plcId);
        _builder.AddTarget(plcId, o => seed(o.InitialValues));
        return this;
    }

    /// <summary>
    /// Configures a target's options directly — timeouts, display name, anything
    /// <see cref="PlcTargetOptions"/> carries.
    /// </summary>
    /// <remarks>
    /// Named distinctly rather than being a third <c>WithTarget</c> overload: two overloads
    /// differing only in their delegate type would make <c>WithTarget("plc1", x =&gt; …)</c>
    /// depend on how the compiler resolves an implicitly-typed lambda against two
    /// candidates. <see cref="PlcTargetOptions.Mode"/> is forced back to
    /// <see cref="ConnectionMode.Simulated"/> regardless of what is set here — a
    /// <see cref="TestPlc"/> never touches hardware.
    /// </remarks>
    /// <param name="plcId">The target identifier, matched case-insensitively.</param>
    /// <param name="configure">Applied to that target's options.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="plcId"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public TestPlcBuilder ConfigureTarget(string plcId, Action<PlcTargetOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        WithTarget(plcId);
        _builder.AddTarget(plcId, configure);
        return this;
    }

    /// <summary>
    /// Routes the pool's logging somewhere visible. Without this the harness is silent,
    /// which is what a passing test wants.
    /// </summary>
    /// <param name="loggerFactory">The factory to log through.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is null.</exception>
    public TestPlcBuilder UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _builder.UseLoggerFactory(loggerFactory);
        return this;
    }

    /// <summary>
    /// Starts the simulated pool. Every target is connected when this returns.
    /// </summary>
    /// <param name="ct">Cancels startup.</param>
    /// <returns>A started harness the caller owns and must dispose.</returns>
    public async Task<TestPlc> StartAsync(CancellationToken ct = default)
    {
        var handle = await _builder.BuildAndStartAsync(ct).ConfigureAwait(false);
        return new TestPlc(handle);
    }
}
