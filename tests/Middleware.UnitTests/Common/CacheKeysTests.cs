using System.Globalization;
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

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("az-Latn-AZ")]
    [InlineData("lt-LT")]
    [InlineData("en-US")]
    public void NormalizeTextLowercasesInvariantlyWhateverTheHostLocale(string culture)
    {
        // The value normalized here is passed to IProductSource as well as into the key, so a
        // culture-sensitive fold would change which upstream call is made on a Turkish-locale host:
        // "ISTANBUL".ToLower() is "ıstanbul" under tr-TR. An en-US CI machine would never notice
        // the difference, which is exactly why this test names the cultures.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            Assert.Equal("istanbul", CacheKeys.NormalizeText("ISTANBUL"));
            Assert.Equal("search|istanbul|0|20", CacheKeys.Search("ISTANBUL", 0, 20));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void SearchKeyIncludesNormalizedQueryAndPagination() =>
        Assert.Equal("search|phone|1|20", CacheKeys.Search("  Phone ", 1, 20));

    [Fact]
    public void FilterPageKeyIncludesNormalizedCategoryAndPagination() =>
        Assert.Equal("page||0|20", CacheKeys.FilterPage("  ", 0, 20));

    [Fact]
    public void FilterCandidatesKeyIsTheNormalizedCategoryAlone() =>
        Assert.Equal("cand|beauty", CacheKeys.FilterCandidates("  Beauty "));

    [Fact]
    public void FilterCandidatesKeyForAnAbsentCategoryIsStable() =>
        Assert.Equal(CacheKeys.FilterCandidates(null), CacheKeys.FilterCandidates("   "));

    [Fact]
    public void FilterPageAndFilterCandidatesKeySpacesNeverCollide()
    {
        // Both live in the same physical cache; the prefixes must keep them disjoint.
        string page = CacheKeys.FilterPage("beauty", 0, 20);
        string candidates = CacheKeys.FilterCandidates("beauty");
        Assert.StartsWith("page|", page);
        Assert.StartsWith("cand|", candidates);
        Assert.NotEqual(page, candidates);
    }

}
