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
}
