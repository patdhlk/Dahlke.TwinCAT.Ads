namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Unit tests for <see cref="SymbolDumpFilter.ShouldInclude"/>.
/// No PLC or hosted service involvement — purely functional.
/// </summary>
public class SymbolDumpFilterTests
{
    // ------------------------------------------------------------------
    // Empty prefixes — depth is the only gate
    // ------------------------------------------------------------------

    [Fact]
    public void EmptyPrefixes_IncludesSymbol_WhenDepthWithinMaxDepth()
    {
        var opts = new SymbolDumpOptions { MaxDepth = 2, Prefixes = [] };

        // depth 0: "GVL"          → 0 dots
        // depth 1: "GVL.MyVar"    → 1 dot
        // depth 2: "GVL.Sub.Val"  → 2 dots
        Assert.True(SymbolDumpFilter.ShouldInclude("GVL", opts));
        Assert.True(SymbolDumpFilter.ShouldInclude("GVL.MyVar", opts));
        Assert.True(SymbolDumpFilter.ShouldInclude("GVL.Sub.Val", opts));
    }

    [Fact]
    public void EmptyPrefixes_ExcludesSymbol_WhenDepthExceedsMaxDepth()
    {
        var opts = new SymbolDumpOptions { MaxDepth = 1, Prefixes = [] };

        // depth 2: two dots — exceeds MaxDepth=1
        Assert.False(SymbolDumpFilter.ShouldInclude("GVL.Sub.Val", opts));
    }

    [Fact]
    public void EmptyPrefixes_IncludesSymbol_AtExactlyMaxDepth()
    {
        var opts = new SymbolDumpOptions { MaxDepth = 2, Prefixes = [] };

        // "A.B.C" has exactly 2 dots → depth == MaxDepth → should be included
        Assert.True(SymbolDumpFilter.ShouldInclude("A.B.C", opts));
    }

    [Fact]
    public void EmptyPrefixes_ExcludesSymbol_AtMaxDepthPlusOne()
    {
        var opts = new SymbolDumpOptions { MaxDepth = 2, Prefixes = [] };

        // "A.B.C.D" has 3 dots → depth == MaxDepth+1 → should be excluded
        Assert.False(SymbolDumpFilter.ShouldInclude("A.B.C.D", opts));
    }

    // ------------------------------------------------------------------
    // Prefix filtering
    // ------------------------------------------------------------------

    [Fact]
    public void PrefixFilter_IncludesSymbol_WhenMatchingPrefix()
    {
        var opts = new SymbolDumpOptions
        {
            MaxDepth = 5,
            Prefixes = ["GVL_Visu", "PRGMain"],
        };

        Assert.True(SymbolDumpFilter.ShouldInclude("GVL_Visu.Button1", opts));
        Assert.True(SymbolDumpFilter.ShouldInclude("PRGMain.State", opts));
    }

    [Fact]
    public void PrefixFilter_ExcludesSymbol_WhenNoMatchingPrefix()
    {
        var opts = new SymbolDumpOptions
        {
            MaxDepth = 5,
            Prefixes = ["GVL_Visu", "PRGMain"],
        };

        Assert.False(SymbolDumpFilter.ShouldInclude("EPLC.SomeVar", opts));
    }

    [Fact]
    public void PrefixFilter_IsCaseInsensitive()
    {
        var opts = new SymbolDumpOptions
        {
            MaxDepth = 3,
            Prefixes = ["gvl_visu"],
        };

        // Upper-case path should match the lower-case prefix
        Assert.True(SymbolDumpFilter.ShouldInclude("GVL_Visu.Button", opts));
        // Mixed-case path should also match
        Assert.True(SymbolDumpFilter.ShouldInclude("GVL_VISU.Led1", opts));
    }

    [Fact]
    public void PrefixFilter_ExcludesSymbol_WhenMatchesPrefixButTooDeep()
    {
        var opts = new SymbolDumpOptions
        {
            MaxDepth = 1,
            Prefixes = ["GVL_Visu"],
        };

        // "GVL_Visu.Sub.Deep" has depth 2, which exceeds MaxDepth=1
        Assert.False(SymbolDumpFilter.ShouldInclude("GVL_Visu.Sub.Deep", opts));
    }

    [Fact]
    public void PrefixFilter_IncludesExactPrefixMatch_AtTopLevel()
    {
        var opts = new SymbolDumpOptions
        {
            MaxDepth = 0,
            Prefixes = ["GVL"],
        };

        // "GVL" itself: depth 0, starts with "GVL"
        Assert.True(SymbolDumpFilter.ShouldInclude("GVL", opts));
    }

    // ------------------------------------------------------------------
    // Depth boundary precision
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("A", 0, true)]          // 0 dots ≤ MaxDepth=0
    [InlineData("A.B", 0, false)]       // 1 dot  > MaxDepth=0
    [InlineData("A.B", 1, true)]        // 1 dot  ≤ MaxDepth=1
    [InlineData("A.B.C", 1, false)]     // 2 dots > MaxDepth=1
    [InlineData("A.B.C", 2, true)]      // 2 dots ≤ MaxDepth=2
    [InlineData("A.B.C.D", 2, false)]   // 3 dots > MaxDepth=2
    public void DepthBoundary_Theory(string path, int maxDepth, bool expected)
    {
        var opts = new SymbolDumpOptions { MaxDepth = maxDepth, Prefixes = [] };
        Assert.Equal(expected, SymbolDumpFilter.ShouldInclude(path, opts));
    }
}
