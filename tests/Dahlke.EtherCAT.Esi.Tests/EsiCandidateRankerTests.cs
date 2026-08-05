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
    /// A slice of a real Beckhoff ESI set, in the shape that matters: one file per family digit,
    /// plus the two files that put an <c>x</c> where that digit would go. Beckhoff writes the
    /// TwinSAFE I/O terminals — EK1914, EL1904, EL2904, EP1908 — into a single cross-family
    /// "Beckhoff ELx9xx.xml" rather than into each family's own file.
    /// </summary>
    private static readonly string[] BeckhoffSlice =
    [
        "/esi/Beckhoff EL19xx.xml",
        "/esi/Beckhoff EL1xxx.xml",
        "/esi/Beckhoff EL2xxx.xml",
        "/esi/Beckhoff EL32xx.xml",
        "/esi/Beckhoff EL69xx.xml",
        "/esi/Beckhoff EL6xxx.xml",
        "/esi/Beckhoff EL7xxx.xml",
        "/esi/Beckhoff EL9xxx.xml",
        "/esi/Beckhoff ELM9xxx.xml",
        "/esi/Beckhoff ELXxxxx.xml",
        "/esi/Beckhoff ELx9xx.xml",
    ];

    // Issue #63: "ELx9xx" shares only "EL" with "EL1904", and 'x' sorts after every digit, so the
    // one file that actually holds the device ranked LAST of its own family group — candidate #39
    // of 142 on the reference set, 605 MB streamed, 3335 ms. Reading the x as the digit it stands
    // for is what turns that into a near-immediate hit.
    //
    // Stated as the property rather than a position: nothing from a family the model does not
    // belong to may be opened before the file that holds it. What remains ahead of it is EL19xx
    // and EL1xxx, both of which spell out the model's own family digit and are the right first
    // guesses — so on the reference set this is candidate #3 of 142, not #39.
    [Fact]
    public void Rank_reads_an_x_in_a_file_name_as_the_digit_it_stands_for()
    {
        var ranked = EsiCandidateRanker.Rank(BeckhoffSlice, "EL1904");

        ranked.TakeWhile(path => path != "/esi/Beckhoff ELx9xx.xml")
            .Should().OnlyContain(path => path.Contains("Beckhoff EL1"));
    }

    // Every TwinSAFE I/O terminal is filed this way, in its own leading-letter variant of the same
    // name — which is what makes the miss a class of devices rather than one unlucky slave.
    [Theory]
    [InlineData("EK1914", "/esi/Beckhoff EKx9xx.xml")]
    [InlineData("EL2904", "/esi/Beckhoff ELx9xx.xml")]
    [InlineData("EP1908", "/esi/Beckhoff EPx9xx.xml")]
    public void Rank_reaches_every_safety_terminals_file_early(string model, string expected)
    {
        string[] files = [.. BeckhoffSlice, "/esi/Beckhoff EKx9xx.xml", "/esi/Beckhoff EPx9xx.xml"];

        EsiCandidateRanker.Rank(files, model).Take(2).Should().Contain(expected);
    }

    // The wildcard widens what scores, so it must not let a vague name outrank a precise one: for
    // EL1904 all three of EL19xx, ELx9xx and ELXxxxx match to the model's full length, and only
    // the name tiebreak then separates them — most digits spelled out first.
    [Fact]
    public void Rank_prefers_the_file_that_spells_out_more_of_the_model()
    {
        EsiCandidateRanker.Rank(BeckhoffSlice, "EL1904")
            .Should().ContainInOrder(
                "/esi/Beckhoff EL19xx.xml",
                "/esi/Beckhoff ELx9xx.xml",
                "/esi/Beckhoff ELXxxxx.xml");
    }

    // The terminal that resolved in 35 ms on the reference rack, because TwinSAFE LOGIC lives in a
    // normally-named file. Widening the score must not cost it its first-candidate hit.
    [Fact]
    public void Rank_still_puts_a_normally_named_family_file_first()
    {
        EsiCandidateRanker.Rank(BeckhoffSlice, "EL6910")[0]
            .Should().Be("/esi/Beckhoff EL69xx.xml");
    }

    // A hint carrying the order number runs past the end of a six-character family name, so the
    // seven-character ELXxxxx can only stay behind EL19xx if its trailing 'x' is barred from
    // matching the '-'. Unbounded, the vaguest name in the set would win on length alone.
    [Fact]
    public void Rank_matches_an_x_only_where_the_model_has_a_digit()
    {
        EsiCandidateRanker.Rank(BeckhoffSlice, "EL1904-0000")
            .Should().ContainInOrder(
                "/esi/Beckhoff EL19xx.xml",
                "/esi/Beckhoff ELx9xx.xml",
                "/esi/Beckhoff ELXxxxx.xml");
    }

    // The ELX (intrinsically safe) family owns the file named for it: 'x' is a wildcard, but an
    // 'X' the model spells out is a literal match for the 'X' in ELXxxxx.
    [Fact]
    public void Rank_puts_the_ELX_family_file_first_for_an_ELX_model()
    {
        EsiCandidateRanker.Rank(BeckhoffSlice, "ELX3181")[0]
            .Should().Be("/esi/Beckhoff ELXxxxx.xml");
    }

    [Fact]
    public void Rank_returns_every_file_exactly_once_with_wildcard_names_in_the_set()
    {
        var ranked = EsiCandidateRanker.Rank(BeckhoffSlice, "EL1904");

        ranked.Should().HaveCount(BeckhoffSlice.Length);
        ranked.Should().OnlyHaveUniqueItems();
        ranked.Should().BeEquivalentTo(BeckhoffSlice);
    }
}
