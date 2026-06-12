using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Proves that <see cref="AdsConnectionPool"/> requests a separate
/// <see cref="ILogger"/> for each <see cref="AdsConnectionFacade"/> it constructs,
/// using the facade's own category name rather than the pool's. This ensures that
/// operators filtering by log category can distinguish facade-originated warnings
/// (state-handler exceptions, dropped typed notifications) from pool-management
/// noise.
/// </summary>
public class AdsConnectionPoolLoggerCategoryTests
{
    /// <summary>
    /// A minimal <see cref="ILoggerFactory"/> that records every category name
    /// passed to <see cref="CreateLogger"/>. Used to assert which categories the
    /// pool requests at construction time.
    /// </summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _categories = new();

        /// <summary>All category names for which a logger was requested, in order.</summary>
        public IReadOnlyList<string> Categories => _categories;

        public ILogger CreateLogger(string categoryName)
        {
            _categories.Add(categoryName);
            return NullLogger.Instance;
        }

        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    [Fact]
    public void PoolConstructor_RequestsLoggerForFacadeCategory_PerConfiguredTarget()
    {
        // Arrange: two configured targets so we get two facade loggers.
        var targets = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["plc1"] = new PlcTargetOptions { DisplayName = "PLC 1", AmsNetId = "1.2.3.4.5.6" },
            ["plc2"] = new PlcTargetOptions { DisplayName = "PLC 2", AmsNetId = "6.5.4.3.2.1" },
        };
        var adsOptions = new TwinCatAdsOptions { Targets = targets };

        var capturingFactory = new CapturingLoggerFactory();

        // Act: constructing the pool is sufficient — facades are created eagerly.
        _ = new AdsConnectionPool(
            Options.Create(adsOptions),
            connectionFactory: null!,   // not invoked during construction
            readySignal: new AdsRouterReadySignal(),
            loggerFactory: capturingFactory,
            timeProvider: new FakeTimeProvider());

        // Assert: the pool requests one logger for itself (AdsConnectionPool) and
        // one per target for the facade (AdsConnectionFacade). With two targets
        // that is three total CreateLogger calls: 1 pool + 2 facades.
        var facadeCategory = typeof(AdsConnectionFacade).FullName!;
        var poolCategory   = typeof(AdsConnectionPool).FullName!;

        Assert.Contains(poolCategory, capturingFactory.Categories);
        Assert.Equal(
            2,
            capturingFactory.Categories.Count(c => c == facadeCategory));
        // The pool's own logger must also be present exactly once.
        Assert.Equal(
            1,
            capturingFactory.Categories.Count(c => c == poolCategory));
    }
}
