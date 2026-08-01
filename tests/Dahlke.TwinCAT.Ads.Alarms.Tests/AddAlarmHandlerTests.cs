using Microsoft.Extensions.DependencyInjection;

namespace Dahlke.TwinCAT.Ads.Alarms.Tests;

/// <summary>
/// Tests for <c>AddAlarmHandler</c> as a registration: what lands in the container and with
/// what lifetime.
/// </summary>
/// <remarks>
/// What the registration is FOR — a handler that cannot miss the snapshot delivered during
/// subscription registration — is proved against a driven connection in
/// <see cref="PlcAlarmMonitorConcurrencyTests"/>, since no container assertion can show it.
/// </remarks>
public class AddAlarmHandlerTests
{
    private sealed class First : IPlcAlarmHandler
    {
        public Task OnTransitionAsync(AlarmTransition transition, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class Second : IPlcAlarmHandler
    {
        public Task OnTransitionAsync(AlarmTransition transition, CancellationToken ct) =>
            Task.CompletedTask;
    }

    [Fact]
    public void RegisteringTheSameHandlerTwice_DeliversToItOnce()
    {
        // TryAddEnumerable dedupes on (service type, implementation type). Plain Add would not,
        // and a library registering its own handler beside an application that registered the
        // same one defensively would page twice for one alarm.
        var services = new ServiceCollection();

        services.AddAlarmHandler<First>();
        services.AddAlarmHandler<First>();

        using var provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<IPlcAlarmHandler>());
    }

    [Fact]
    public void DistinctHandlerTypes_AreBothRegistered()
    {
        var services = new ServiceCollection();

        services.AddAlarmHandler<First>();
        services.AddAlarmHandler<Second>();

        using var provider = services.BuildServiceProvider();

        var handlers = provider.GetServices<IPlcAlarmHandler>().ToList();

        Assert.Equal(2, handlers.Count);
        Assert.Contains(handlers, h => h is First);
        Assert.Contains(handlers, h => h is Second);
    }

    [Fact]
    public void Handlers_AreSingletons()
    {
        // The monitor is a singleton and resolves these from the ROOT provider. Anything
        // scoped would throw there rather than quietly getting a fresh instance per
        // transition, so the lifetime is part of the contract, not an implementation detail.
        var services = new ServiceCollection();

        services.AddAlarmHandler<First>();

        using var provider = services.BuildServiceProvider();

        Assert.Same(
            Assert.Single(provider.GetServices<IPlcAlarmHandler>()),
            Assert.Single(provider.GetServices<IPlcAlarmHandler>()));
    }

    [Fact]
    public void AHandlerRegisteredWithoutThisExtension_IsStillCollected()
    {
        // IPlcAlarmHandler is an ordinary enumerable seam, which is what lets a handler that
        // needs constructor arguments be registered by hand. Documented on AddAlarmHandler;
        // asserted here so the two cannot drift.
        var services = new ServiceCollection();

        services.AddSingleton<IPlcAlarmHandler>(_ => new First());

        using var provider = services.BuildServiceProvider();

        Assert.IsType<First>(Assert.Single(provider.GetServices<IPlcAlarmHandler>()));
    }

    [Fact]
    public void AddAlarmHandler_ReturnsTheCollectionForChaining()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddAlarmHandler<First>());
    }

    [Fact]
    public void AddAlarmHandler_RejectsANullCollection() =>
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddAlarmHandler<First>());
}
