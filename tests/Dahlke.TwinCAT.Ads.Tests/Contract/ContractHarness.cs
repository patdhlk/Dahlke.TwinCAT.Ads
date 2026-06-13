namespace Dahlke.TwinCAT.Ads.Tests.Contract;

/// <summary>
/// The minimal surface a contract test needs from an <see cref="IAdsConnection"/>
/// implementation under test.
/// </summary>
/// <remarks>
/// <para>
/// Kept deliberately tight — three members, no more than the [Fact]s in
/// <see cref="AdsConnectionContractTests"/> consume:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="Connection"/> — the <see cref="IAdsConnection"/> the contract asserts against.
///   </description></item>
///   <item><description>
///     <see cref="WriteRawAsync"/> — a backdoor for ARRANGING store state without going through
///     the public typed/untyped write API under test. For the simulated harness it writes
///     directly into the sim's store; for the facade harness it writes into the underlying
///     in-memory connection's store. In both cases it fires on-change subscriptions, so a
///     subscription [Fact] can arrange a notification through the same backdoor it uses to seed
///     read [Fact]s — one consistent path, no per-implementation special-casing.
///   </description></item>
///   <item><description>
///     <see cref="DisposeAsync"/> — tears down the connection (and, for the facade, the pushed
///     in-memory connection) after each [Fact].
///   </description></item>
/// </list>
/// </remarks>
public sealed record ContractHarness(
    IAdsConnection Connection,
    Func<string, object?, Task> WriteRaw,
    Func<ValueTask> Dispose) : IAsyncDisposable
{
    /// <summary>Arranges store state for the symbol at <paramref name="path"/>.</summary>
    public Task WriteRawAsync(string path, object? value) => WriteRaw(path, value);

    public ValueTask DisposeAsync() => Dispose();
}
