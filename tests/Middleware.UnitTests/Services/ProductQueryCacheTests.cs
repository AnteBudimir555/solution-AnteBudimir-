using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Middleware.Core.Abstractions;
using Middleware.Core.Domain;
using Middleware.Core.Exceptions;
using Middleware.Core.Options;
using Middleware.Core.Services;
using Middleware.UnitTests.Support;
using NSubstitute;

namespace Middleware.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ProductQueryCache"/>. Each test uses a fresh, empty <see cref="HybridCache"/>
/// so every call is a cache miss and runs the factory: the focus is the wrapped logic — input
/// normalization, the <c>page*size</c> offset translation, the empty-category routing and the in-memory
/// price filter (including the null-price exclusion). Single-flight/caching behaviour itself is verified
/// in <c>ProductQueryCacheSingleFlightTests</c>, and the price-capable source path in
/// <c>PriceCapableSourceTests</c>.
/// </summary>
public class ProductQueryCacheTests
{
    private readonly IProductSource _source = Substitute.For<IProductSource>();
    private readonly HybridCache _cache;

    public ProductQueryCacheTests()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        _cache = services.BuildServiceProvider().GetRequiredService<HybridCache>();
        // The substitute defaults to false, which is DummyJSON's answer; stated so the default that
        // puts every test below on the in-memory path is visible rather than incidental.
        _source.SupportsPriceFilter.Returns(false);
    }

    /// <summary>The unbounded candidate fetch the in-memory price path issues.</summary>
    private static ProductQuery CandidateQuery(string? category = null) =>
        new() { Category = category, Limit = null };

    private ProductQueryCache Cache() => WithThreshold(5000);

    private ProductQueryCache WithThreshold(int threshold) =>
        new(_source, _cache, Options.Create(new UpstreamOptions { MaxInMemoryCandidates = threshold }),
            Options.Create(new CacheOptions()), NullLogger<ProductQueryCache>.Instance);

    private void ReturnsForCandidates(ProductQuery query, IReadOnlyList<Product> catalog) =>
        _source.QueryAsync(query, Arg.Any<CancellationToken>())
            .Returns(new ProductQueryResult(new ProductPage(catalog, catalog.Count, 0, 0), false));

    [Fact]
    public async Task SearchNormalizesQueryAndTranslatesOffset()
    {
        var expected = new ProductQuery { NameContains = "phone", Skip = 20, Limit = 10 };
        _source.QueryAsync(expected, Arg.Any<CancellationToken>())
            .Returns(new ProductQueryResult(new ProductPage([], 0, 20, 10), false));

        var result = await Cache().SearchAsync("  Phone ", 2, 10);

        Assert.Equal(20, result.Skip);
        Assert.Equal(10, result.Limit);
        await _source.Received(1).QueryAsync(expected, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CategoryPageWithBlankCategoryFallsBackToFullList()
    {
        // Blank normalizes to "", which the query object spells as "no category filter" — null.
        var expected = new ProductQuery { Category = null, Skip = 0, Limit = 20 };
        _source.QueryAsync(expected, Arg.Any<CancellationToken>())
            .Returns(new ProductQueryResult(new ProductPage([], 0, 0, 20), false));

        var result = await Cache().CategoryPageAsync("   ", 0, 20);

        Assert.Equal(20, result.Limit);
        await _source.Received(1).QueryAsync(expected, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CategoryPageWithCategoryQueriesThatCategory()
    {
        var expected = new ProductQuery { Category = "beauty", Skip = 10, Limit = 10 };
        _source.QueryAsync(expected, Arg.Any<CancellationToken>())
            .Returns(new ProductQueryResult(new ProductPage([], 0, 10, 10), false));

        var result = await Cache().CategoryPageAsync("Beauty", 1, 10);

        Assert.Equal(10, result.Skip);
        await _source.Received(1).QueryAsync(expected, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PriceFilteredPageKeepsOnlyPricesWithinBoundsAndDropsNullPrices()
    {
        List<Product> catalog =
        [
            TestData.Product(1, 5m),
            TestData.Product(2, 10m),
            TestData.Product(3, 30m),
            TestData.Product(4, 50m),
            TestData.Product(5, 80m),
            TestData.Product(6, null)   // must be excluded, never throw
        ];
        ReturnsForCandidates(CandidateQuery(), catalog);

        var filtered = await WithThreshold(5000).PriceFilteredPageAsync(null, 10m, 50m, 0, 20);

        Assert.Equal(new long[] { 2, 3, 4 }, filtered.Items.Select(p => p.Id));
        Assert.Equal(3, filtered.Total);
    }

    [Fact]
    public async Task PriceFilteredPageTreatsBoundsAsInclusiveAndOpenEnded()
    {
        List<Product> catalog = [TestData.Product(1, 10m), TestData.Product(2, 100m), TestData.Product(3, 1000m)];
        ReturnsForCandidates(CandidateQuery("beauty"), catalog);

        // Only a lower bound -> everything >= 100 (inclusive).
        var minOnly = await WithThreshold(5000).PriceFilteredPageAsync("Beauty", 100m, null, 0, 20);

        Assert.Equal(new long[] { 2, 3 }, minOnly.Items.Select(p => p.Id));
    }

    [Fact]
    public async Task PriceFilteredPagePaginatesTheFilteredSetAndReportsTheFilteredTotal()
    {
        // Pagination moved out of ProductService with the query reshape; the total must stay the count
        // of everything that matched, not of the page returned, or PagedResponse reports one page.
        List<Product> catalog = [.. Enumerable.Range(1, 10).Select(i => TestData.Product(i, i * 10m))];
        ReturnsForCandidates(CandidateQuery(), catalog);

        var secondPage = await WithThreshold(5000).PriceFilteredPageAsync(null, 20m, 90m, 1, 3);

        Assert.Equal(new long[] { 5, 6, 7 }, secondPage.Items.Select(p => p.Id));
        Assert.Equal(8, secondPage.Total);
        Assert.Equal(3, secondPage.Skip);
    }

    [Fact]
    public async Task PriceFilteredPagePastTheEndOfTheFilteredSetIsEmptyRatherThanThrowing()
    {
        List<Product> catalog = [TestData.Product(1, 5m), TestData.Product(2, 50m)];
        ReturnsForCandidates(CandidateQuery(), catalog);

        var page = await WithThreshold(5000).PriceFilteredPageAsync(null, 1m, 100m, 50, 20);

        Assert.Empty(page.Items);
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task PriceFilteredPageRefusesToLoadMoreThanTheInMemoryThreshold()
    {
        List<Product> catalog = [TestData.Product(1, 5m), TestData.Product(2, 50m)];
        ReturnsForCandidates(CandidateQuery(), catalog);

        // The threshold is a limit, not a log line: over it, the fetch is refused rather than
        // materializing an unbounded catalog on a request a client can repeat at will.
        var refused = await Assert.ThrowsAsync<UpstreamException>(
            async () => await WithThreshold(1).PriceFilteredPageAsync(null, null, 10m, 0, 20));

        Assert.Contains("over the configured limit", refused.Message);
    }

    /// <summary>
    /// The bound that makes the candidate cache safe to expose to clients: price bounds are
    /// client-supplied decimals with unlimited distinct values, so if they reached the cache key every
    /// new pair would miss and pull the entire upstream catalog. They are applied in memory over one
    /// cached set instead, and the upstream sees a single fetch however the bounds are varied.
    /// </summary>
    [Fact]
    public async Task VaryingPriceBoundsShareOneUpstreamFetch()
    {
        List<Product> catalog = [TestData.Product(1, 5m), TestData.Product(2, 50m)];
        ReturnsForCandidates(CandidateQuery(), catalog);
        var cache = Cache();

        for (int cents = 0; cents < 200; cents++)
        {
            await cache.PriceFilteredPageAsync(null, 10m + (cents * 0.01m), 50m, 0, 20);
        }

        await _source.Received(1).QueryAsync(CandidateQuery(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The candidate query must carry no price bounds at all — not merely bounds the source ignores.
    /// The key covers the category alone, so anything else the call depended on would be a value two
    /// requests could differ in while sharing an entry.
    /// </summary>
    [Fact]
    public async Task TheCandidateFetchCarriesNoPriceBounds()
    {
        List<Product> catalog = [TestData.Product(1, 5m)];
        ReturnsForCandidates(CandidateQuery(), catalog);

        await Cache().PriceFilteredPageAsync(null, 10m, 50m, 0, 20);

        await _source.Received(1).QueryAsync(
            Arg.Is<ProductQuery>(q => !q.HasPriceFilter && q.Limit == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CategoriesAreFetchedOnceAndReturnedVerbatim()
    {
        _source.CategoriesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)new[] { "beauty", "laptops" });
        var cache = Cache();

        var first = await cache.CategoriesAsync();
        var second = await cache.CategoriesAsync();

        Assert.Equal(new[] { "beauty", "laptops" }, first);
        Assert.Equal(new[] { "beauty", "laptops" }, second);
        await _source.Received(1).CategoriesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The categories entry is cached as a marked record so the cache can hand back the stored
    /// instance instead of deserializing on every hit. Measured on Hybrid 10.8.0, the obvious
    /// alternatives — the bare <c>IReadOnlyList&lt;string&gt;</c>, <c>string[]</c>, an unmarked record,
    /// even <c>ImmutableArray&lt;string&gt;</c> — all return a fresh instance per hit. Reference
    /// identity is the only observable proof, so it is what this asserts.
    /// </summary>
    [Fact]
    public async Task CategoriesAreServedWithoutDeserializingOnEveryHit()
    {
        _source.CategoriesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)new[] { "beauty", "laptops" });
        var cache = Cache();

        var first = await cache.CategoriesAsync();
        var second = await cache.CategoriesAsync();

        Assert.Same(first, second);
    }

    [Fact]
    public async Task PriceBoundsStillFilterTheCachedCandidateSetPerCall()
    {
        List<Product> catalog = [TestData.Product(1, 5m), TestData.Product(2, 50m)];
        ReturnsForCandidates(CandidateQuery(), catalog);
        var cache = Cache();

        // Sharing one cached fetch must not mean sharing one answer: each call filters it afresh.
        var cheap = await cache.PriceFilteredPageAsync(null, null, 10m, 0, 20);
        var dear = await cache.PriceFilteredPageAsync(null, 10m, null, 0, 20);

        Assert.Equal(new long[] { 1 }, cheap.Items.Select(p => p.Id));
        Assert.Equal(new long[] { 2 }, dear.Items.Select(p => p.Id));
    }
}
