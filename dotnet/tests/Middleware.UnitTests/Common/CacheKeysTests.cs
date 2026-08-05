using Middleware.Core.Common;

namespace Middleware.UnitTests.Common;

/// <summary>
/// Port of the Java <c>CacheKeysTest</c>. The cache correctness contract: parameter sets that produce
/// the same upstream call must collapse to the same key (case/whitespace, numeric scale, absent
/// bounds), and the two filter-cache key spaces must never collide.
/// </summary>
public class CacheKeysTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void NormalizeTextCollapsesNullAndBlankToEmpty(string? input) =>
        Assert.Equal(string.Empty, CacheKeys.NormalizeText(input));

    [Theory]
    [InlineData("Beauty", "beauty")]
    [InlineData("  Smartphones  ", "smartphones")]
    [InlineData("HOME-DECOR", "home-decor")]
    public void NormalizeTextTrimsAndLowercases(string input, string expected) =>
        Assert.Equal(expected, CacheKeys.NormalizeText(input));

    [Fact]
    public void SearchKeyIncludesNormalizedQueryAndPagination() =>
        Assert.Equal("search|phone|1|20", CacheKeys.Search("  Phone ", 1, 20));

    [Fact]
    public void FilterPageKeyIncludesNormalizedCategoryAndPagination() =>
        Assert.Equal("page||0|20", CacheKeys.FilterPage("  ", 0, 20));

    [Fact]
    public void FilterCandidatesKeyUsesStarForAbsentBounds() =>
        Assert.Equal("cand|beauty|*|*", CacheKeys.FilterCandidates("Beauty", null, null));

    [Fact]
    public void FilterCandidatesKeyNormalizesEquivalentNumericScales()
    {
        string a = CacheKeys.FilterCandidates("beauty", 10.00m, 50m);
        string b = CacheKeys.FilterCandidates("beauty", 10m, 50.0000m);
        Assert.Equal("cand|beauty|10|50", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void FilterPageAndFilterCandidatesKeySpacesNeverCollide()
    {
        // Both live in the same physical cache; the prefixes must keep them disjoint.
        string page = CacheKeys.FilterPage("beauty", 0, 20);
        string candidates = CacheKeys.FilterCandidates("beauty", null, null);
        Assert.StartsWith("page|", page);
        Assert.StartsWith("cand|", candidates);
        Assert.NotEqual(page, candidates);
    }
}
