using System.Text;
using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TwinCAT.Ads.TcpRouter;
using AmsNetId = TwinCAT.Ads.AmsNetId;
using Route = TwinCAT.Ads.Configuration.Route;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Remote routes for the embedded AMS router: binding from configuration, reaching
/// the router, and failing the host on a malformed entry.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these cover, measured against a live rack.</b>
/// <see cref="AdsRouterService"/> started an <c>AmsTcpIpRouter</c> with no remote
/// routes, and <see cref="AmsRouterOptions"/> exposed only
/// <see cref="AmsRouterOptions.NetId"/>, so no host could tell it how to reach a
/// PLC. The identical code path failed with <c>TargetMachineNotFound</c> without a
/// route and succeeded with one — same machine, same Net ID, one variable. Four
/// candidate configuration spellings were measured against the
/// <c>AmsTcpIpRouter(IConfiguration, …)</c> overload and all yielded zero routes,
/// and Beckhoff's only other source is a TwinCAT <c>StaticRoutes.xml</c> on disk,
/// absent on a machine without a TwinCAT installation. So on Linux and macOS a host
/// could not reach a remote PLC at all.
/// </para>
/// <para>
/// <b>Binding cases use <c>AddJsonStream</c>, not <c>AddInMemoryCollection</c>.</b>
/// The two providers do not agree about an absent value: the JSON provider renders a
/// JSON <c>null</c> as an EMPTY STRING, while an in-memory collection holds a real
/// CLR <see langword="null"/> that the binder SKIPS, leaving the property at its
/// default. An in-memory test can therefore assert the opposite of what a host with
/// an <c>appsettings.json</c> actually gets. JSON is also the shape a host writes, so
/// every binding case here goes through the real JSON provider.
/// </para>
/// <para>
/// <b>Why binding needs its own tests at all.</b> The <c>AmsRouter</c> section is
/// bound PROPERTY-BY-PROPERTY rather than with a whole-section <c>Bind</c>, and
/// <c>OptionsSectionsAreBoundTests</c> explicitly does not cover a new member of a
/// sub-object bound that way — verified by mutation. Without the cases below,
/// <see cref="AmsRouterOptions.Routes"/> could ship reading nothing from
/// configuration, exactly as <c>RawChannels</c> once did.
/// </para>
/// </remarks>
public class AmsRouterRouteTests
{
    /// <summary>
    /// A minimal valid PLC target. Startup validation rejects an empty
    /// <c>Targets</c> collection, so every case needs one; it is scaffolding, not
    /// part of what is under test.
    /// </summary>
    private const string ScaffoldTargets = """{ "plc1": { "AmsNetId": "1.2.3.4.5.6" } }""";

    private static IConfiguration JsonConfiguration(string json)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    private static TwinCatAdsOptions ResolveFromJson(string json)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(JsonConfiguration(json));

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;
    }

    // ------------------------------------------------------------------
    // Binding
    // ------------------------------------------------------------------

    /// <summary>
    /// The whole route arrives — name, Net ID and address — from the documented JSON
    /// shape. This is the case that was red before the binding existed: the section
    /// bound only <c>NetId</c>, so <c>Routes</c> came back empty.
    /// </summary>
    [Fact]
    public void SingleRoute_BindsWholeFromConfiguration()
    {
        var options = ResolveFromJson($$"""
            {
              "PlcTargets": {{ScaffoldTargets}},
              "AmsRouter": {
                "NetId": "192.168.1.220.1.1",
                "Routes": [
                  { "Name": "rack", "NetId": "5.138.44.199.1.1", "Address": "192.168.1.223" }
                ]
              }
            }
            """);

        Assert.Equal("192.168.1.220.1.1", options.Router.NetId);

        var route = Assert.Single(options.Router.Routes);
        Assert.Equal("rack", route.Name);
        Assert.Equal("5.138.44.199.1.1", route.NetId);
        Assert.Equal("192.168.1.223", route.Address);
    }

    /// <summary>
    /// Two routes both arrive, in the order they were written.
    /// </summary>
    /// <remarks>
    /// Order is asserted rather than membership because the router's route table is
    /// keyed by name and a host reading its own log expects the entries in the order
    /// it declared them. A binder that reversed or reordered a list would be a
    /// surprise worth failing on.
    /// </remarks>
    [Fact]
    public void TwoRoutes_BothBind_InOrder()
    {
        var options = ResolveFromJson($$"""
            {
              "PlcTargets": {{ScaffoldTargets}},
              "AmsRouter": {
                "NetId": "192.168.1.220.1.1",
                "Routes": [
                  { "Name": "rack",  "NetId": "5.138.44.199.1.1", "Address": "192.168.1.223" },
                  { "Name": "bench", "NetId": "5.20.30.40.1.1",   "Address": "cx-01a2b3" }
                ]
              }
            }
            """);

        Assert.Equal(2, options.Router.Routes.Count);

        Assert.Equal("rack", options.Router.Routes[0].Name);
        Assert.Equal("5.138.44.199.1.1", options.Router.Routes[0].NetId);
        Assert.Equal("192.168.1.223", options.Router.Routes[0].Address);

        Assert.Equal("bench", options.Router.Routes[1].Name);
        Assert.Equal("5.20.30.40.1.1", options.Router.Routes[1].NetId);

        // A host NAME is as valid as an IP: Beckhoff's route type resolves either.
        Assert.Equal("cx-01a2b3", options.Router.Routes[1].Address);
    }

    /// <summary>
    /// An absent <c>Routes</c> key leaves an EMPTY list, never
    /// <see langword="null"/> — so every consumer can enumerate without a guard.
    /// </summary>
    [Fact]
    public void AbsentRoutesSection_LeavesAnEmptyList()
    {
        var options = ResolveFromJson($$"""
            {
              "PlcTargets": {{ScaffoldTargets}},
              "AmsRouter": { "NetId": "192.168.1.220.1.1" }
            }
            """);

        Assert.NotNull(options.Router.Routes);
        Assert.Empty(options.Router.Routes);
    }

    /// <summary>
    /// An absent <c>AmsRouter</c> section altogether leaves the same empty list, and
    /// a <see langword="null"/> <c>NetId</c> — the "use the system router" default.
    /// </summary>
    [Fact]
    public void AbsentRouterSection_LeavesAnEmptyList()
    {
        var options = ResolveFromJson($$"""
            {
              "PlcTargets": {{ScaffoldTargets}}
            }
            """);

        Assert.Null(options.Router.NetId);
        Assert.Empty(options.Router.Routes);
    }

    /// <summary>
    /// The combo overload's documented ordering applied to routes: binding first,
    /// then the lambda. A lambda that appends a route must not be erased by the
    /// bind.
    /// </summary>
    [Fact]
    public void ComboOverload_LambdaAddsToTheBoundRoutes()
    {
        var configuration = JsonConfiguration($$"""
            {
              "PlcTargets": {{ScaffoldTargets}},
              "AmsRouter": {
                "NetId": "192.168.1.220.1.1",
                "Routes": [
                  { "Name": "rack", "NetId": "5.138.44.199.1.1", "Address": "192.168.1.223" }
                ]
              }
            }
            """);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(configuration, o => o.Router.Routes.Add(new AmsRouteOptions
        {
            Name = "bench",
            NetId = "5.20.30.40.1.1",
            Address = "192.168.1.224",
        }));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        Assert.Equal(2, options.Router.Routes.Count);
        Assert.Contains(options.Router.Routes, r => r.Name == "rack");
        Assert.Contains(options.Router.Routes, r => r.Name == "bench");
    }

    /// <summary>
    /// A route element the binder DISCARDS must fail the host by name rather than
    /// leaving the device silently unreachable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No route member is convertible-typed, so a failed conversion cannot happen —
    /// but that is not the only way an element is dropped. An element written as a
    /// SCALAR where an object belongs cannot bind to a complex type and is discarded
    /// with no error at all, exactly as a raw-channel seed SLOT is. Someone writing
    /// routes as bare names or bare addresses is the plausible route to it.
    /// </para>
    /// <para>
    /// Written against JSON because it is the only provider that can express a scalar
    /// element sitting where an object belongs.
    /// </para>
    /// </remarks>
    [Theory]
    // Every element a bare value: nothing binds at all.
    [InlineData("""[ "rack", "bench" ]""")]
    // One scalar among objects: the list binds one route short.
    [InlineData("""[ "rack", { "Name": "bench", "NetId": "5.20.30.40.1.1", "Address": "1.2.3.4" } ]""")]
    public void RouteDiscardedByTheBinder_FailsAtStartup(string routesLiteral)
    {
        var configuration = JsonConfiguration($$"""
            {
              "PlcTargets": {{ScaffoldTargets}},
              "AmsRouter": { "NetId": "192.168.1.220.1.1", "Routes": {{routesLiteral}} }
            }
            """);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TwinCatAdsOptions>>();

        var ex = Assert.Throws<OptionsValidationException>(() => _ = options.Value);

        Assert.Contains(
            ex.Failures,
            f => f.Contains("AmsRouter:Routes") && f.Contains("DISCARDED"));
    }

    /// <summary>
    /// Registering twice must not report a false discard, and must not produce two
    /// copies of one route — which would then fail the duplicate-name check.
    /// </summary>
    /// <remarks>
    /// This is why <c>Routes</c> is ASSIGNED by the binding step rather than
    /// Bind-appended as <c>RawChannels:Seed</c> is: <c>Bind</c> APPENDS to a list, so
    /// an appending bind plus a duplicate-name rule would fail every host that calls
    /// <c>AddTwinCatAds</c> twice.
    /// </remarks>
    [Fact]
    public void DoubleRegistration_KeepsOneCopyOfEachRoute()
    {
        var configuration = JsonConfiguration($$"""
            {
              "PlcTargets": {{ScaffoldTargets}},
              "AmsRouter": {
                "NetId": "192.168.1.220.1.1",
                "Routes": [
                  { "Name": "rack", "NetId": "5.138.44.199.1.1", "Address": "192.168.1.223" }
                ]
              }
            }
            """);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(configuration);
        services.AddTwinCatAds(configuration);

        using var provider = services.BuildServiceProvider();

        // Resolving is half the assertion: a duplicate name would throw here.
        var options = provider.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        Assert.Equal("rack", Assert.Single(options.Router.Routes).Name);
    }

    // ------------------------------------------------------------------
    // Reaching the router
    // ------------------------------------------------------------------

    /// <summary>
    /// Records every route handed to <c>TryAddRoute</c> and controls the answer.
    /// </summary>
    /// <remarks>
    /// <c>AmsTcpIpRouter</c> cannot be faked — it is sealed-in-effect for testing
    /// purposes and binds a real TCP listener — so
    /// <see cref="AdsRouterService.ApplyConfiguredRoutes"/> takes the router's
    /// <c>TryAddRoute</c> as a delegate. This stands in for it.
    /// </remarks>
    private sealed class RouteRecorder
    {
        private readonly Func<Route, bool> _answer;

        public RouteRecorder(Func<Route, bool>? answer = null) =>
            _answer = answer ?? (_ => true);

        public List<Route> Added { get; } = [];

        public bool TryAddRoute(Route route)
        {
            Added.Add(route);
            return _answer(route);
        }
    }

    private static AdsRouterService RouterService(
        RecordingLoggerProvider log,
        string? netId,
        params AmsRouteOptions[] routes)
    {
        var options = new TwinCatAdsOptions
        {
            Targets = new(StringComparer.OrdinalIgnoreCase)
            {
                ["plc1"] = new PlcTargetOptions { Mode = ConnectionMode.Real, AmsNetId = "1.2.3.4.5.6" },
            },
            Router = new AmsRouterOptions { NetId = netId, Routes = [.. routes] },
        };

        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(log).SetMinimumLevel(LogLevel.Trace));

        return new AdsRouterService(
            Options.Create(options),
            configuration: null,
            loggerFactory,
            new AdsRouterReadySignal(),
            TimeProvider.System);
    }

    /// <summary>
    /// The proof that binding is not the whole job: a configured route is actually
    /// handed to the router, whole, in order, from the <c>RouterStatus.Started</c>
    /// hook.
    /// </summary>
    /// <remarks>
    /// This is the half-fix guard. Binding could work perfectly while nothing ever
    /// reached <c>TryAddRoute</c> — the host would start clean, the router would run,
    /// and every operation against the device would answer
    /// <c>TargetMachineNotFound</c>, which is the defect this task exists to remove.
    /// </remarks>
    [Fact]
    public void StartedRouter_ReceivesEveryConfiguredRoute_InOrder()
    {
        var log = new RecordingLoggerProvider();
        var recorder = new RouteRecorder();
        var signal = new AdsRouterReadySignal();

        var service = RouterService(
            log,
            "192.168.1.220.1.1",
            new AmsRouteOptions { Name = "rack", NetId = "5.138.44.199.1.1", Address = "192.168.1.223" },
            new AmsRouteOptions { Name = "bench", NetId = "5.20.30.40.1.1", Address = "cx-01a2b3" });

        service.HandleRouterStatusChanged(RouterStatus.Started, signal, recorder.TryAddRoute);

        Assert.Equal(2, recorder.Added.Count);

        Assert.Equal("rack", recorder.Added[0].Name);
        Assert.Equal("5.138.44.199.1.1", recorder.Added[0].NetId.ToString());
        Assert.Equal("192.168.1.223", recorder.Added[0].Address);

        Assert.Equal("bench", recorder.Added[1].Name);
        Assert.Equal("5.20.30.40.1.1", recorder.Added[1].NetId.ToString());
        Assert.Equal("cx-01a2b3", recorder.Added[1].Address);

        // Each is logged at Information naming all three values, so an operator can
        // tell from the log alone which routes the router is actually carrying.
        Assert.Contains(
            log.Entries,
            e => e.Level == LogLevel.Information
                 && e.Message.Contains("rack")
                 && e.Message.Contains("5.138.44.199.1.1")
                 && e.Message.Contains("192.168.1.223"));
    }

    /// <summary>
    /// Routes are in the table BEFORE the readiness signal resolves.
    /// </summary>
    /// <remarks>
    /// The signal is what releases the connection pool's real-target loops. Resolving
    /// it first would let a pool connection race a route that is not registered yet,
    /// reintroducing <c>TargetMachineNotFound</c> intermittently — the worst version
    /// of this bug, because it would pass every test that did not look at ordering.
    /// </remarks>
    [Fact]
    public async Task RoutesAreAdded_BeforeTheReadySignalResolves()
    {
        var log = new RecordingLoggerProvider();
        var signal = new AdsRouterReadySignal();

        var routesWereAddedFirst = false;
        var recorder = new RouteRecorder(_ =>
        {
            // Ready yet? It must not be — the routes are still going in.
            routesWereAddedFirst = !signal.WaitAsync(CancellationToken.None).IsCompleted;
            return true;
        });

        var service = RouterService(
            log,
            "192.168.1.220.1.1",
            new AmsRouteOptions { Name = "rack", NetId = "5.138.44.199.1.1", Address = "192.168.1.223" });

        service.HandleRouterStatusChanged(RouterStatus.Started, signal, recorder.TryAddRoute);

        Assert.True(routesWereAddedFirst, "the ready signal resolved before the routes were added");

        // And it did resolve: the hook still does its original job.
        await signal.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// A status other than <c>Started</c> adds nothing and resolves nothing.
    /// </summary>
    [Fact]
    public void NonStartedStatus_AddsNoRoutes()
    {
        var log = new RecordingLoggerProvider();
        var recorder = new RouteRecorder();
        var signal = new AdsRouterReadySignal();

        var service = RouterService(
            log,
            "192.168.1.220.1.1",
            new AmsRouteOptions { Name = "rack", NetId = "5.138.44.199.1.1", Address = "192.168.1.223" });

        service.HandleRouterStatusChanged(RouterStatus.Stopped, signal, recorder.TryAddRoute);

        Assert.Empty(recorder.Added);
        Assert.False(signal.WaitAsync(CancellationToken.None).IsCompleted);
    }

    /// <summary>
    /// A route the router REJECTS is logged at Warning naming it, and does not stop
    /// the remaining routes from being added.
    /// </summary>
    /// <remarks>
    /// Rejection is logged rather than thrown on purpose: throwing would re-enter the
    /// retry loop and tear down a router that is otherwise working, so one unreachable
    /// device would cost every reachable one. Warning is then the ONLY signal an
    /// operator gets, which is why its content is asserted.
    /// </remarks>
    [Fact]
    public void RejectedRoute_IsLoggedAtWarning_AndDoesNotStopTheRest()
    {
        var log = new RecordingLoggerProvider();
        var signal = new AdsRouterReadySignal();
        var recorder = new RouteRecorder(r => r.Name != "rack");

        var service = RouterService(
            log,
            "192.168.1.220.1.1",
            new AmsRouteOptions { Name = "rack", NetId = "5.138.44.199.1.1", Address = "192.168.1.223" },
            new AmsRouteOptions { Name = "bench", NetId = "5.20.30.40.1.1", Address = "192.168.1.224" });

        service.HandleRouterStatusChanged(RouterStatus.Started, signal, recorder.TryAddRoute);

        Assert.Equal(2, recorder.Added.Count);

        Assert.Contains(
            log.Entries,
            e => e.Level == LogLevel.Warning
                 && e.Message.Contains("REJECTED")
                 && e.Message.Contains("rack"));

        // The accepted one is still reported as added, not swept up in the failure.
        Assert.Contains(
            log.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("bench"));
    }

    /// <summary>
    /// A Net ID with an out-of-range octet is SKIPPED rather than handed to the
    /// router, even though startup validation should already have rejected it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AmsNetId.Parse</c> does not throw on <c>999.1.1.1.1.1</c> — it ZEROES the
    /// bad octet and yields <c>0.1.1.1.1.1</c>. Handing that to the router would
    /// register a route addressing a device the operator never named, which is worse
    /// than no route at all: it fails somewhere else, silently.
    /// </para>
    /// <para>
    /// The service is constructed directly here, bypassing options validation, which
    /// is the only way to reach this branch. That is the point — it is a
    /// defence-in-depth check, and the test says so rather than pretending the
    /// configuration path can produce it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("999.1.1.1.1.1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("")]
    public void LaunderableNetId_IsSkippedAndWarned_RatherThanHandedToTheRouter(string netId)
    {
        var log = new RecordingLoggerProvider();
        var recorder = new RouteRecorder();
        var signal = new AdsRouterReadySignal();

        var service = RouterService(
            log,
            "192.168.1.220.1.1",
            new AmsRouteOptions { Name = "rack", NetId = netId, Address = "192.168.1.223" });

        service.HandleRouterStatusChanged(RouterStatus.Started, signal, recorder.TryAddRoute);

        Assert.Empty(recorder.Added);
        Assert.Contains(
            log.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("Skipping route 'rack'"));
    }

    /// <summary>
    /// Routes configured while the embedded router is DISABLED are announced as
    /// ignored, not dropped in silence.
    /// </summary>
    /// <remarks>
    /// An <c>AmsRouter</c> section carrying <c>Routes</c> but no <c>NetId</c> is an
    /// easy thing to write — and the routes then have nowhere to go, because the
    /// system router keeps its own table. Validation cannot reject it (running against
    /// a system router with leftover routes is legitimate), so a warning is the only
    /// place the operator can find out.
    /// </remarks>
    [Fact]
    public async Task RoutesWithNoEmbeddedRouter_AreWarnedAboutRatherThanIgnoredSilently()
    {
        var log = new RecordingLoggerProvider();

        var service = RouterService(
            log,
            netId: null,
            new AmsRouteOptions { Name = "rack", NetId = "5.138.44.199.1.1", Address = "192.168.1.223" });

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Contains(
            log.Entries,
            e => e.Level == LogLevel.Warning
                 && e.Message.Contains("Ignoring 1 configured AmsRouter:Routes")
                 && e.Message.Contains("AmsRouter:NetId"));
    }

    // ------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------

    private static OptionsValidationException ValidationFailure(AmsRouteOptions[] routes)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
            o.Router.NetId = "192.168.1.220.1.1";
            o.Router.Routes = [.. routes];
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TwinCatAdsOptions>>();

        return Assert.Throws<OptionsValidationException>(() => _ = options.Value);
    }

    /// <summary>
    /// A Net ID with an out-of-range octet FAILS the host, rather than being laundered
    /// into a different device's address.
    /// </summary>
    /// <remarks>
    /// <b>Measured, not folklore:</b> <c>AmsNetId.TryParse("999.1.1.1.1.1")</c> returns
    /// <see langword="true"/> and yields <c>0.1.1.1.1.1</c> — the octet is ZEROED, so
    /// <c>256</c>, <c>300</c> and <c>999</c> all collapse to the same address.
    /// Validation therefore uses <c>RawSeedParser.IsWellFormedNetId</c>, shared with
    /// raw-channel seed validation, so the two can never drift apart. Delegating to
    /// Beckhoff would let this configuration start a host whose route points somewhere
    /// nobody wrote down.
    /// </remarks>
    [Theory]
    [InlineData("999.1.1.1.1.1")]
    [InlineData("256.1.1.1.1.1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.3.4.5.6.7")]
    [InlineData("")]
    [InlineData("not-a-netid")]
    public void MalformedRouteNetId_FailsAtStartup(string netId)
    {
        var ex = ValidationFailure([
            new AmsRouteOptions { Name = "rack", NetId = netId, Address = "192.168.1.223" },
        ]);

        Assert.Contains(ex.Failures, f => f.Contains("AmsRouter:Routes:0:NetId"));
    }

    /// <summary>
    /// Beckhoff's own parser really does launder the value this validator rejects, so
    /// the choice not to delegate is load-bearing rather than defensive taste.
    /// </summary>
    /// <remarks>
    /// Pinned as an executable fact because the whole argument for
    /// <c>RawSeedParser.IsWellFormedNetId</c> rests on it. If a future Beckhoff version
    /// starts rejecting the value instead, this test says so and the reasoning in the
    /// validator can be revisited deliberately.
    /// </remarks>
    [Fact]
    public void AmsNetIdTryParse_LaundersAnOutOfRangeOctet()
    {
        Assert.True(AmsNetId.TryParse("999.1.1.1.1.1", out var laundered));
        Assert.Equal("0.1.1.1.1.1", laundered.ToString());

        Assert.False(RawSeedParser.IsWellFormedNetId("999.1.1.1.1.1"));
    }

    /// <summary>
    /// A route needs a name — the router's route table is keyed by it, and
    /// <c>RemoveRoute(string)</c> takes one.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingRouteName_FailsAtStartup(string name)
    {
        var ex = ValidationFailure([
            new AmsRouteOptions { Name = name, NetId = "5.138.44.199.1.1", Address = "192.168.1.223" },
        ]);

        Assert.Contains(ex.Failures, f => f.Contains("AmsRouter:Routes:0:Name is required"));
    }

    /// <summary>
    /// A route needs an address; there is no default that could mean anything.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingRouteAddress_FailsAtStartup(string address)
    {
        var ex = ValidationFailure([
            new AmsRouteOptions { Name = "rack", NetId = "5.138.44.199.1.1", Address = address },
        ]);

        Assert.Contains(ex.Failures, f => f.Contains("AmsRouter:Routes:0:Address is required"));
    }

    /// <summary>
    /// Two routes sharing a name fail the host, because the router keys its table by
    /// name and so they are not two routes.
    /// </summary>
    /// <remarks>
    /// Compared case-INSENSITIVELY. Whether Beckhoff's table is case sensitive was not
    /// measured, and the two readings differ only in which of a pair of near-identical
    /// names survives — a coin flip an operator cannot predict either way. Rejecting
    /// both spellings costs a host nothing it wanted.
    /// </remarks>
    [Theory]
    [InlineData("rack", "rack")]
    [InlineData("rack", "RACK")]
    public void DuplicateRouteName_FailsAtStartup(string first, string second)
    {
        var ex = ValidationFailure([
            new AmsRouteOptions { Name = first, NetId = "5.138.44.199.1.1", Address = "192.168.1.223" },
            new AmsRouteOptions { Name = second, NetId = "5.20.30.40.1.1", Address = "192.168.1.224" },
        ]);

        Assert.Contains(
            ex.Failures,
            f => f.Contains("AmsRouter:Routes:1:Name") && f.Contains("duplicates"));
    }

    /// <summary>
    /// Every failure in a route is reported at once, and each names its own index —
    /// the same all-failures-together standard the rest of this validator meets.
    /// </summary>
    [Fact]
    public void EveryRouteFailure_IsReportedTogether_WithItsOwnIndex()
    {
        var ex = ValidationFailure([
            new AmsRouteOptions { Name = "", NetId = "999.1.1.1.1.1", Address = "" },
            new AmsRouteOptions { Name = "bench", NetId = "5.20.30.40.1.1", Address = "192.168.1.224" },
            new AmsRouteOptions { Name = "bench", NetId = "nope", Address = "192.168.1.225" },
        ]);

        Assert.Contains(ex.Failures, f => f.Contains("AmsRouter:Routes:0:Name"));
        Assert.Contains(ex.Failures, f => f.Contains("AmsRouter:Routes:0:NetId"));
        Assert.Contains(ex.Failures, f => f.Contains("AmsRouter:Routes:0:Address"));
        Assert.Contains(ex.Failures, f => f.Contains("AmsRouter:Routes:2:Name"));
        Assert.Contains(ex.Failures, f => f.Contains("AmsRouter:Routes:2:NetId"));

        // The good entry is not blamed for its neighbours. Matched on the message's
        // OPENING path rather than a substring, because the duplicate-name failure for
        // entry 2 legitimately names entry 1 as the first use — being cited is not
        // being blamed, and a substring match cannot tell the two apart.
        Assert.DoesNotContain(ex.Failures, f => f.StartsWith("AmsRouter:Routes:1:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Routes are validated even when the embedded router is DISABLED, so a typo left
    /// behind after switching to the system router still fails the host rather than
    /// waiting silently for someone to switch back.
    /// </summary>
    [Fact]
    public void MalformedRoute_FailsAtStartup_EvenWithNoEmbeddedRouter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
            // No Router.NetId: the system router is used and Routes are ignored.
            o.Router.Routes = [
                new AmsRouteOptions { Name = "rack", NetId = "999.1.1.1.1.1", Address = "192.168.1.223" },
            ];
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TwinCatAdsOptions>>();

        var ex = Assert.Throws<OptionsValidationException>(() => _ = options.Value);
        Assert.Contains(ex.Failures, f => f.Contains("AmsRouter:Routes:0:NetId"));
    }

    /// <summary>
    /// A well-formed set of routes passes — so the failures above are really about the
    /// defects they name and not about routes being rejected wholesale.
    /// </summary>
    [Fact]
    public void WellFormedRoutes_PassValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(o =>
        {
            o.Targets["plc1"] = new PlcTargetOptions { AmsNetId = "1.2.3.4.5.6" };
            o.Router.NetId = "192.168.1.220.1.1";
            o.Router.Routes = [
                new AmsRouteOptions { Name = "rack", NetId = "5.138.44.199.1.1", Address = "192.168.1.223" },
                // A host name is as valid as an IP address.
                new AmsRouteOptions { Name = "bench", NetId = "5.20.30.40.1.1", Address = "cx-01a2b3" },
            ];
        });

        using var provider = services.BuildServiceProvider();

        // Resolving is the assertion: any failure throws here.
        var options = provider.GetRequiredService<IOptions<TwinCatAdsOptions>>().Value;

        Assert.Equal(2, options.Router.Routes.Count);
    }
}
