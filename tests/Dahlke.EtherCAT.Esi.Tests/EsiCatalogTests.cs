using Dahlke.EtherCAT.Esi;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dahlke.EtherCAT.Esi.Tests;

public class EsiCatalogTests
{
    private const uint Beckhoff = 2;
    private const uint El3204 = 0x0C843052;
    private const uint Rev1 = 0x00100000;
    private const string TypeHint = "EL3204";

    private static string FixtureDir(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static EsiCatalog Catalog(
        string? directory,
        int budgetMs = 5000,
        ILogger<EsiCatalog>? logger = null,
        TimeProvider? timeProvider = null) =>
        new(
            Options.Create(new EsiOptions { Directory = directory, LookupBudgetMs = budgetMs }),
            // A do-nothing logger, not an NSubstitute mock: EsiCatalog is internal (see EsiCatalog's
            // narrowing to internal), and NSubstitute's Castle-backed proxy for a closed
            // ILogger<EsiCatalog> needs the DEFINING assembly to grant InternalsVisibleTo to
            // Castle's own dynamic proxy assembly, not just to this test assembly — which it does
            // not, and should not have to just to support a logger nothing here asserts on.
            logger ?? NullLogger<EsiCatalog>.Instance,
            timeProvider);

    /// <summary>
    /// A typed logger writing into the suite's existing <see cref="CapturingLoggerProvider"/>, so
    /// these tests assert on captured level + message rather than hand-rolling another fake.
    /// </summary>
    private static (ILogger<EsiCatalog> Logger, CapturingLoggerProvider Captured) CapturingLogger()
    {
        var provider = new CapturingLoggerProvider();
        ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));

        return (factory.CreateLogger<EsiCatalog>(), provider);
    }

    [Fact]
    public async Task LookupAsync_reports_not_configured_when_no_directory_is_set()
    {
        var result = await Catalog(null).LookupAsync(new EsiKey(Beckhoff, El3204, Rev1), TypeHint);

        result.Status.Should().Be(EsiStatus.NotConfigured);
        result.Device.Should().BeNull();
    }

    [Fact]
    public async Task LookupAsync_reports_not_configured_when_the_directory_does_not_exist()
    {
        var result = await Catalog("/no/such/esi/directory")
            .LookupAsync(new EsiKey(Beckhoff, El3204, Rev1), TypeHint);

        result.Status.Should().Be(EsiStatus.NotConfigured);
    }

    [Fact]
    public async Task LookupAsync_resolves_a_device_from_the_fixture_directory()
    {
        var result = await Catalog(FixtureDir("Esi"))
            .LookupAsync(new EsiKey(Beckhoff, El3204, Rev1), TypeHint);

        result.Status.Should().Be(EsiStatus.Resolved);
        result.Device.Should().NotBeNull();
        result.Device!.NameEn.Should().Be("EL3204 4Ch. Ana. Input PT100 (RTD)");
        result.Device.VendorName.Should().Be("Beckhoff Automation GmbH & Co. KG");
        result.Device.Group.Should().Be("Analog Input");
    }

    // The hint names the wrong family, so the ranked candidates all miss and only the fallback
    // scan through the remaining files can find the device. This is the test that would fail if
    // EsiCandidateRanker filtered instead of ordering.
    [Fact]
    public async Task LookupAsync_finds_a_device_the_type_hint_ranks_away_from()
    {
        var result = await Catalog(FixtureDir("Esi"))
            .LookupAsync(new EsiKey(Beckhoff, El3204, Rev1), "Vendor(0x1234)");

        result.Status.Should().Be(EsiStatus.Resolved);
    }

    [Fact]
    public async Task LookupAsync_reports_not_found_for_an_identity_absent_from_the_set()
    {
        var result = await Catalog(FixtureDir("Esi"))
            .LookupAsync(new EsiKey(Beckhoff, 0x99993052, Rev1), "EL9999");

        result.Status.Should().Be(EsiStatus.NotFound);
        result.Device.Should().BeNull();
    }

    [Fact]
    public async Task LookupAsync_reports_read_failed_when_the_only_candidate_is_malformed()
    {
        var result = await Catalog(FixtureDir("EsiMalformed"))
            .LookupAsync(new EsiKey(Beckhoff, El3204, Rev1), TypeHint);

        result.Status.Should().Be(EsiStatus.ReadFailed);
        result.Device.Should().BeNull();
    }

    // The third outcome of a scan, uncovered until now: matched DESPITE an earlier file failing to
    // parse. "Beckhoff EL3204-corrupt.xml" is a copy of EsiMalformed's truncated file, named so
    // EsiCandidateRanker's type-hint heuristic ranks it BEFORE "Beckhoff EL32xx.xml" for the
    // "EL3204" hint (its stripped name shares a full 6-character common prefix with the model,
    // versus EL32xx's 4) — so the catalog is guaranteed to hit and skip the bad file before ever
    // reaching the good one, rather than this test passing only because the ranking happened to
    // avoid the corrupt file.
    [Fact]
    public async Task LookupAsync_resolves_a_device_despite_an_earlier_file_failing_to_parse()
    {
        var result = await Catalog(FixtureDir("EsiMixed"))
            .LookupAsync(new EsiKey(Beckhoff, El3204, Rev1), TypeHint);

        result.Status.Should().Be(EsiStatus.Resolved);
        result.Device.Should().NotBeNull();
        result.Device!.NameEn.Should().Be("EL3204 4Ch. Ana. Input PT100 (RTD)");
    }

    [Fact]
    public async Task LookupAsync_reports_not_found_and_warns_when_the_budget_is_exhausted()
    {
        (ILogger<EsiCatalog> logger, CapturingLoggerProvider captured) = CapturingLogger();

        var result = await Catalog(FixtureDir("Esi"), budgetMs: 0, logger: logger)
            .LookupAsync(new EsiKey(Beckhoff, El3204, Rev1), TypeHint);

        result.Status.Should().Be(EsiStatus.NotFound);

        // budgetMs: 0 also fires the CONSTRUCTOR's once-per-process "cannot bound anything"
        // Warning (see Constructor_warns_once_when_a_configured_directory_has_a_non_positive_budget
        // below), so two Warnings are expected here, not one. This assertion is scoped to the
        // PER-LOOKUP Warning specifically, by its distinct "before searching every candidate file"
        // wording.
        captured.Entries.Where(e => e.Level >= LogLevel.Warning &&
                e.Message.Contains("before searching every candidate file"))
            .Should().ContainSingle()
            .Which.Message.Should().Contain("LookupBudgetMs");
    }

    // #61 adds this Warning to EsiCatalog's constructor: a configured directory that DOES exist
    // paired with a non-positive LookupBudgetMs is silently inert otherwise — every lookup would
    // report notFound with nothing in the log naming why. Emitted from the constructor (not
    // per-lookup) so it stays once-per-process, mirroring EtherCatMonitor's own
    // non-positive-budget Warning.
    [Fact]
    public void Constructor_warns_once_when_a_configured_directory_has_a_non_positive_budget()
    {
        (ILogger<EsiCatalog> logger, CapturingLoggerProvider captured) = CapturingLogger();

        _ = Catalog(FixtureDir("Esi"), budgetMs: 0, logger: logger);

        captured.Entries.Where(e => e.Level >= LogLevel.Warning)
            .Should().ContainSingle()
            .Which.Message.Should().Contain("LookupBudgetMs");
    }

    // A RESOLVABLE key on purpose. The unresolved statuses are static singletons, so two separate
    // resolves would return the same instance whether or not the cache worked — only a resolved
    // lookup, which allocates a fresh result, makes reference equality prove anything.
    [Fact]
    public async Task LookupAsync_serves_the_same_result_instance_from_cache()
    {
        var catalog = Catalog(FixtureDir("Esi"));
        var key = new EsiKey(Beckhoff, El3204, Rev1);

        var first = await catalog.LookupAsync(key, TypeHint);
        var second = await catalog.LookupAsync(key, TypeHint);

        first.Status.Should().Be(EsiStatus.Resolved);
        second.Should().BeSameAs(first, "a second lookup must not re-parse the file");
    }

    [Fact]
    public async Task LookupAsync_logs_a_missing_device_once_not_once_per_lookup()
    {
        (ILogger<EsiCatalog> logger, CapturingLoggerProvider captured) = CapturingLogger();
        var catalog = Catalog(FixtureDir("Esi"), logger: logger);
        var key = new EsiKey(Beckhoff, 0x99993052, Rev1);

        await catalog.LookupAsync(key, "EL9999");
        await catalog.LookupAsync(key, "EL9999");
        await catalog.LookupAsync(key, "EL9999");

        captured.Entries.Should().HaveCount(1);
    }

    // Task.Run + the eager `[.. ]` spread is load-bearing: Task.WhenAll enumerates a lazy
    // Select sequentially on the calling thread, and LookupAsync is not async, so without
    // eagerly spreading every Task.Run into an array before awaiting, the first call would
    // publish the Lazy and finish before the second even started — the test would pass with
    // the Lazy removed.
    [Fact]
    public async Task LookupAsync_serves_concurrent_first_lookups_from_one_resolve()
    {
        (ILogger<EsiCatalog> logger, CapturingLoggerProvider captured) = CapturingLogger();
        var catalog = Catalog(FixtureDir("Esi"), logger: logger);
        var key = new EsiKey(Beckhoff, 0x99993052, Rev1);

        Task<EsiLookupResult>[] lookups = [.. Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => catalog.LookupAsync(key, "EL9999")))];

        await Task.WhenAll(lookups);

        captured.Entries.Should().HaveCount(1);
    }

    // The only other budget test (LookupAsync_reports_not_found_and_warns_when_the_budget_is_
    // exhausted) uses budgetMs: 0, which an implementation that checked the budget ONCE BEFORE
    // the loop — rather than between files, as LookupBudget's doc comment claims — would satisfy
    // identically. This exercises the injectable TimeProvider seam directly: FakeTimeProvider's
    // AutoAdvanceAmount makes every clock read self-advance, so the sequence of reads the
    // between-files check performs (one for the initial snapshot, one more per file considered)
    // is what proves the check happens PER FILE. With AutoAdvanceAmount = 3000 ms and a 5000 ms
    // budget: the snapshot read returns t=0; the check before file 1 reads t=3000 (elapsed 3000
    // < 5000, budget intact, file 1 is opened); the check before file 2 reads t=6000 (elapsed
    // 6000 >= 5000, exhausted) — so file 2, which holds the sought identity, must never be
    // opened. A once-before-the-loop implementation would perform only the first of these reads
    // and go on to open (and resolve from) file 2 regardless.
    [Fact]
    public async Task LookupAsync_checks_the_budget_between_files_not_once_up_front()
    {
        var clock = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromMilliseconds(3000) };
        (ILogger<EsiCatalog> logger, CapturingLoggerProvider captured) = CapturingLogger();

        // "Beckhoff EL0002.xml" carries the sought EL3204 identity; ranking (hint "EL0001")
        // guarantees it is considered strictly after "Beckhoff EL0001.xml", so it is only ever
        // reached if the budget check does NOT fire between the two files.
        var result = await Catalog(FixtureDir("EsiBudget"), budgetMs: 5000, logger: logger, timeProvider: clock)
            .LookupAsync(new EsiKey(Beckhoff, El3204, Rev1), "EL0001");

        result.Status.Should().Be(EsiStatus.NotFound);
        captured.Entries.Where(e => e.Level >= LogLevel.Warning)
            .Should().ContainSingle()
            .Which.Message.Should().Contain("LookupBudgetMs");
    }
}
