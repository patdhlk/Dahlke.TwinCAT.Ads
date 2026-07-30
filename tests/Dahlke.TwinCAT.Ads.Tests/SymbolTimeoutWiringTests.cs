using Microsoft.Extensions.Logging.Abstractions;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Pins that the Beckhoff client's own timeout can never preempt a configured
/// symbol-layer bound.
/// </summary>
/// <remarks>
/// Before this was wired, <c>AdsClient.Timeout</c> was never assigned anywhere in
/// the symbol layer, so Beckhoff's invisible 5000 ms default capped everything —
/// making <see cref="PlcTargetOptions.SymbolBrowseTimeoutMs"/>'s 30000 default
/// unreachable out of the box.
/// </remarks>
public class SymbolTimeoutWiringTests
{
    private static AdsConnection Create(int timeoutMs, int browseMs) =>
        new("plc1",
            new PlcTargetOptions
            {
                AmsNetId = "1.2.3.4.5.6",
                Port = 851,
                TimeoutMs = timeoutMs,
                SymbolBrowseTimeoutMs = browseMs,
            },
            NullLoggerFactory.Instance);

    [Theory]
    [InlineData(1000, 30000)]   // the shipped defaults' shape: browse >> operation
    [InlineData(30000, 1000)]   // inverted, so neither value is special-cased
    [InlineData(750, 750)]      // both below Beckhoff's 5000 default
    public void ClientTimeout_NeverPreemptsEitherConfiguredBound(int timeoutMs, int browseMs)
    {
        using var connection = Create(timeoutMs, browseMs);

        Assert.True(connection.ClientTimeoutMs > timeoutMs,
            $"client timeout {connection.ClientTimeoutMs} would preempt TimeoutMs {timeoutMs}");
        Assert.True(connection.ClientTimeoutMs > browseMs,
            $"client timeout {connection.ClientTimeoutMs} would preempt SymbolBrowseTimeoutMs {browseMs}");
    }

    [Fact]
    public void ClientTimeout_IsNotLeftAtBeckhoffsDefault()
    {
        using var connection = Create(timeoutMs: 1000, browseMs: 30000);

        // 5000 is Beckhoff's default. Leaving it there is the bug this task fixes,
        // and it silently caps the 30000 ms browse bound.
        Assert.NotEqual(5000, connection.ClientTimeoutMs);
    }
}
