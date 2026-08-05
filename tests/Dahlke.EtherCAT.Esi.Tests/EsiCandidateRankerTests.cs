using Dahlke.EtherCAT.Esi;
using FluentAssertions;

namespace Dahlke.EtherCAT.Esi.Tests;

public class EsiCandidateRankerTests
{
    private static readonly string[] Files =
    [
        "/esi/Beckhoff EL31xx.xml",
        "/esi/Beckhoff EL32xx.xml",
        "/esi/Beckhoff EK11xx.xml",
        "/esi/Beckhoff EtherCAT Terminals.xml",
        "/esi/Acme Widgets.xml",
    ];

    [Fact]
    public void Rank_puts_the_closest_family_file_first()
    {
        EsiCandidateRanker.Rank(Files, "EL3204")[0].Should().Be("/esi/Beckhoff EL32xx.xml");
    }

    [Fact]
    public void Rank_ranks_a_nearer_prefix_above_a_same_family_file()
    {
        EsiCandidateRanker.Rank(Files, "EL3204")
            .Should().ContainInOrder("/esi/Beckhoff EL32xx.xml", "/esi/Beckhoff EL31xx.xml");
    }

    [Fact]
    public void Rank_returns_every_file_exactly_once()
    {
        var ranked = EsiCandidateRanker.Rank(Files, "EL3204");

        ranked.Should().HaveCount(Files.Length);
        ranked.Should().OnlyHaveUniqueItems();
        ranked.Should().BeEquivalentTo(Files);
    }

    [Fact]
    public void Rank_orders_the_combined_catalog_ahead_of_unrelated_files()
    {
        EsiCandidateRanker.Rank(Files, "EL3204")
            .Should().ContainInOrder("/esi/Beckhoff EtherCAT Terminals.xml", "/esi/Acme Widgets.xml");
    }

    // DecodeDeviceType returns these for non-Beckhoff vendors, so they are the REAL hint for any
    // non-Beckhoff slave. Neither prefixes an ESI filename, so ranking degrades to "everything,
    // alphabetically" — which must still be complete, or those devices become unfindable.
    [Theory]
    [InlineData("Unknown")]
    [InlineData("Vendor(0x1234)")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rank_still_returns_every_file_for_a_useless_hint(string hint)
    {
        var ranked = EsiCandidateRanker.Rank(Files, hint);

        ranked.Should().HaveCount(Files.Length);
        ranked.Should().OnlyHaveUniqueItems();
    }

    // The test helpers in EtherCatServiceTests build Type as "EL1008 | Test Device", and a real
    // master can return a decorated string too, so only the leading token is the model.
    [Fact]
    public void Rank_uses_only_the_leading_token_of_a_decorated_type_string()
    {
        EsiCandidateRanker.Rank(Files, "EL3204 | 4Ch. Ana. Input")[0]
            .Should().Be("/esi/Beckhoff EL32xx.xml");
    }

    [Fact]
    public void Rank_handles_a_filename_without_the_vendor_prefix()
    {
        string[] files = ["/esi/EL32xx.xml", "/esi/Acme Widgets.xml"];

        EsiCandidateRanker.Rank(files, "EL3204")[0].Should().Be("/esi/EL32xx.xml");
    }

    [Fact]
    public void Rank_returns_empty_for_no_files()
    {
        EsiCandidateRanker.Rank([], "EL3204").Should().BeEmpty();
    }

    /// <summary>
    /// The reference Beckhoff set the measurements in issue #63 were taken against: 142 files,
    /// names verbatim, vendor prefix stripped for width. Only the names matter — <c>Rank</c> is
    /// pure — so ranking against the real set is what lets a test assert the candidate POSITION
    /// the timings depend on, rather than describe it.
    /// </summary>
    private static readonly string[] ReferenceSet =
    [
        .. new[]
        {
            "AF1yxx", "AF1yxx-zzzz-0101", "AF1yxx-zzzz-0102", "AMI8xxx", "AMP86xx", "AMP88xx",
            "APS1xxx", "APS4xxx", "ASI8xxx", "AT21xx", "AT22xx", "AT2xxx", "ATH2xxx", "AX1yxx",
            "AX2xxx", "AX5xxx", "AX8600", "AX86xx", "AX8820", "AX883x", "AX88xx", "AX8yxx",
            "AX8yxx-zzzz-0106", "AX8yxx-zzzz-0107", "BKxxxx", "CUxxxx", "CXxxxx", "EC11xx",
            "ED1xxx", "ED2xxx", "ED3xxx", "ED4xxx", "ED5xxx", "ED6xxx", "ED7xxx", "ED9xxx",
            "EJ1xxx", "EJ2xxx", "EJ3xxx", "EJ3xxx-0030", "EJ4xxx", "EJ5xxx", "EJ6xxx", "EJ7xxx",
            "EJ9xxx", "EJx9xx", "EK10xx", "EK11xx", "EK12xx", "EK13xx", "EK15xx", "EK18xx",
            "EKM1xxx", "EKx9xx", "EKxxxx-0080", "EL15xx", "EL19xx", "EL1xxx", "EL25xx", "EL29xx",
            "EL2xxx", "EL30xx", "EL31xx", "EL32xx", "EL33xx", "EL34xx", "EL37xx", "EL3xxx",
            "EL3xxx-0030", "EL47xx", "EL4xxx", "EL5xxx", "EL66xx", "EL67xx", "EL68xx", "EL69xx",
            "EL6xxx", "EL72xx", "EL73xx", "EL7xxx", "EL8xxx", "EL99xx", "EL9xxx", "ELM300x",
            "ELM310x", "ELM314x", "ELM324x", "ELM334x", "ELM350x", "ELM360x", "ELM370x", "ELM72xx",
            "ELM9xxx", "ELXxxxx", "ELx9xx", "EM2xxx", "EM3xxx", "EM7xxx", "EP1xxx", "EP2xxx",
            "EP3xxx", "EP4xxx", "EP5xxx", "EP6xxx", "EP7xxx", "EP8xxx", "EP9xxx", "EPP1xxx",
            "EPP2xxx", "EPP3xxx", "EPP4xxx", "EPP5xxx", "EPP6xxx", "EPP7xxx", "EPP9xxx", "EPXxxxx",
            "EPx9xx", "EQ1xxx", "EQ2xxx", "EQ3xxx", "ER1xxx", "ER2xxx", "ER3xxx", "ER4xxx",
            "ER5xxx", "ER6xxx", "ER7xxx", "ER8xxx", "ERP3xxx", "ERP6xxx", "EtherCAT EvaBoard",
            "EtherCAT Terminals", "FB1XXX", "FCxxxx", "FM3xxx", "ILxxxx-B110", "MBxxxx", "MOxxxx",
            "MRxxxx", "MSxxxx", "MX8911", "PS2xxx"
        }.Select(n => $"/esi/Beckhoff {n}.xml"),
    ];

    // Issue #63: "ELx9xx" shares only "EL" with "EL1904", and 'x' sorts after every digit, so the
    // one file that actually holds the device ranked LAST of its own family group — candidate 39 of
    // 142, 577 MiB streamed, 3452 ms, 67% of the default budget. Reading the x as the digit it
    // stands for is what turns that into candidate 3, 8 MiB and 98 ms.
    //
    // Asserted as the measured position, against the real set, because the position IS the cost.
    [Fact]
    public void Rank_reads_an_x_in_a_file_name_as_the_digit_it_stands_for()
    {
        var ranked = EsiCandidateRanker.Rank(ReferenceSet, "EL1904");

        ranked.Should().HaveCount(142);
        ranked.ToList().IndexOf("/esi/Beckhoff ELx9xx.xml").Should().Be(2);
    }

    // What stays ahead of it is only ever the model's own family — the two files that spell out
    // the 1 in EL1904 — never an unrelated one. The property behind the number above.
    [Fact]
    public void Rank_opens_nothing_from_another_family_before_the_file_that_holds_the_device()
    {
        EsiCandidateRanker.Rank(ReferenceSet, "EL1904")
            .TakeWhile(path => path != "/esi/Beckhoff ELx9xx.xml")
            .Should().OnlyContain(path => path.StartsWith("/esi/Beckhoff EL1"));
    }

    // Every TwinSAFE I/O terminal is filed this way, in its own leading-letter variant of the same
    // name — which is what makes the miss a class of devices rather than one unlucky slave. All
    // four variants are present in the reference set.
    [Theory]
    [InlineData("EK1914", "/esi/Beckhoff EKx9xx.xml")]
    [InlineData("EL2904", "/esi/Beckhoff ELx9xx.xml")]
    [InlineData("EP1908", "/esi/Beckhoff EPx9xx.xml")]
    [InlineData("EJ1914", "/esi/Beckhoff EJx9xx.xml")]
    public void Rank_reaches_every_safety_terminals_file_early(string model, string expected)
    {
        EsiCandidateRanker.Rank(ReferenceSet, model).Take(3).Should().Contain(expected);
    }

    // The wildcard widens what scores, so it must not let a vague name outrank a precise one: for
    // EL1904 all three of EL19xx, ELx9xx and ELXxxxx match to the model's full length, and only
    // the name tiebreak then separates them — most digits spelled out first.
    [Fact]
    public void Rank_prefers_the_file_that_spells_out_more_of_the_model()
    {
        EsiCandidateRanker.Rank(ReferenceSet, "EL1904")
            .Should().ContainInOrder(
                "/esi/Beckhoff EL19xx.xml",
                "/esi/Beckhoff ELx9xx.xml",
                "/esi/Beckhoff ELXxxxx.xml");
    }

    // The terminal that resolved in tens of ms on the reference rack, because TwinSAFE LOGIC lives
    // in a normally-named file. Widening the score must not cost it its first-candidate hit.
    [Fact]
    public void Rank_still_puts_a_normally_named_family_file_first()
    {
        EsiCandidateRanker.Rank(ReferenceSet, "EL6910")[0]
            .Should().Be("/esi/Beckhoff EL69xx.xml");
    }

    // A hint carrying the order number runs past the end of a six-character family name, so the
    // seven-character ELXxxxx can only stay behind EL19xx if its trailing 'x' is barred from
    // matching the '-'. Unbounded, the vaguest name in the set would win on length alone. Beckhoff
    // ships suffixed names itself ("EKxxxx-0080" is in this set), so the shape is real.
    [Fact]
    public void Rank_matches_an_x_only_where_the_model_has_a_digit()
    {
        EsiCandidateRanker.Rank(ReferenceSet, "EL1904-0000")
            .Should().ContainInOrder(
                "/esi/Beckhoff EL19xx.xml",
                "/esi/Beckhoff ELx9xx.xml",
                "/esi/Beckhoff ELXxxxx.xml");
    }

    // The ELX (intrinsically safe) family owns the file named for it: 'x' is a wildcard, but an
    // 'X' the model spells out is a literal match for the 'X' in ELXxxxx. Case cannot be the
    // discriminator either way — Beckhoff spells wildcards both ways, and this set holds both
    // "ELXxxxx" with its literal X and "FB1XXX" with three uppercase wildcards.
    [Fact]
    public void Rank_puts_the_ELX_family_file_first_for_an_ELX_model()
    {
        EsiCandidateRanker.Rank(ReferenceSet, "ELX3181")[0]
            .Should().Be("/esi/Beckhoff ELXxxxx.xml");
    }

    // Case cannot decide what is a wildcard, because Beckhoff spells them both ways: the set above
    // holds "ELXxxxx", whose X is the literal series letter, and "FB1XXX", whose X's are wildcards.
    // Only the model's own character can decide. A minimal pair states it, since the real set has
    // no second FB file to rank FB1XXX against.
    [Fact]
    public void Rank_reads_an_uppercase_X_as_a_wildcard_too()
    {
        string[] files = ["/esi/Beckhoff FB18xx.xml", "/esi/Beckhoff FB1XXX.xml"];

        EsiCandidateRanker.Rank(files, "FB1005")[0].Should().Be("/esi/Beckhoff FB1XXX.xml");
    }

    [Fact]
    public void Rank_returns_every_file_exactly_once_with_wildcard_names_in_the_set()
    {
        var ranked = EsiCandidateRanker.Rank(ReferenceSet, "EL1904");

        ranked.Should().HaveCount(ReferenceSet.Length);
        ranked.Should().OnlyHaveUniqueItems();
        ranked.Should().BeEquivalentTo(ReferenceSet);
    }
}
