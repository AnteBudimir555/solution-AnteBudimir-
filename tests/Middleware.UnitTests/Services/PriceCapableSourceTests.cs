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

namespace Middleware.UnitTests.Services;

/// <summary>
/// The half of the <see cref="ProductQuery"/> reshape that no source in this repository exercises: what
/// happens when a source declares it can apply the price bounds itself.
///
/// <para>These tests are the reason the capability is not merely documented. Without a source that
/// declares it, <c>SupportsPriceFilter</c> and <c>PriceFilterApplied</c> would be an interface that
/// describes a behaviour nothing performs and nothing checks — the shape <c>Cache:MaximumSize</c> had
/// for months before B3. The fake below is that source.</para>
/// </summary>
public class PriceCapableSourceTests
{
    /// <summary>
    /// A source that filters on price natively, as a SQL- or Elasticsearch-backed one would. It records
    /// every query it is asked, which is what the assertions read.
    /// </summary>
    private sealed class PriceCapableSource(IReadOnlyList<Product> catalog, bool honourPriceFilter = true)
        : IProductSource
    {
        public List<ProductQuery> Queries { get; } = [];

        public bool SupportsPriceFilter => true;

        public Task<ProductQueryResult> QueryAsync(ProductQuery query, CancellationToken ct = default)
        {
            Queries.Add(query);

            var matching = catalog
                .Where(p => p.Price is not null
                            && (query.MinPrice is null || p.Price >= query.MinPrice)
                            && (query.MaxPrice is null || p.Price <= query.MaxPrice))
                .ToList();

            // The store paginates the filtered set, which is the whole point: it never materializes
            // more than the page, and the total is the filtered count.
            var items = matching.Skip(query.Skip).Take(query.Limit ?? matching.Count).ToList();
            return Task.FromResult(new ProductQueryResult(
                new ProductPage(items, matching.Count, query.Skip, query.Limit ?? matching.Count),
                PriceFilterApplied: honourPriceFilter));
        }

        public Task<Product> GetByIdAsync(long id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static List<Product> Catalog() =>
        [.. Enumerable.Range(1, 10).Select(i => TestData.Product(i, i * 10m))];

    private static ProductQueryCache CacheOver(IProductSource source, int threshold = 5000)
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var hybrid = services.BuildServiceProvider().GetRequiredService<HybridCache>();
        return new ProductQueryCache(
            source, hybrid,
            Options.Create(new UpstreamOptions { MaxInMemoryCandidates = threshold }),
            Options.Create(new CacheOptions()), NullLogger<ProductQueryCache>.Instance);
    }

    [Fact]
    public async Task ThePriceBoundsArePushedDownInsteadOfFetchingACandidateSet()
    {
        var source = new PriceCapableSource(Catalog());

        var page = await CacheOver(source).PriceFilteredPageAsync(null, 30m, 60m, 0, 20);

        var query = Assert.Single(source.Queries);
        Assert.Equal(30m, query.MinPrice);
        Assert.Equal(60m, query.MaxPrice);
        // The decisive assertion: a bounded page, not the "give me everything" fetch the in-memory
        // path issues. This is what L4 exists to make possible.
        Assert.Equal(20, query.Limit);
        Assert.Equal(new long[] { 3, 4, 5, 6 }, page.Items.Select(p => p.Id));
        Assert.Equal(4, page.Total);
    }

    [Fact]
    public async Task PaginationIsPushedDownToo()
    {
        var source = new PriceCapableSource(Catalog());

        var page = await CacheOver(source).PriceFilteredPageAsync(null, 10m, 100m, 2, 3);

        var query = Assert.Single(source.Queries);
        Assert.Equal(6, query.Skip);
        Assert.Equal(3, query.Limit);
        Assert.Equal(new long[] { 7, 8, 9 }, page.Items.Select(p => p.Id));
        Assert.Equal(10, page.Total);
    }

    [Fact]
    public async Task IdenticalPriceQueriesShareOneCallToTheSource()
    {
        var source = new PriceCapableSource(Catalog());
        var cache = CacheOver(source);

        await cache.PriceFilteredPageAsync("beauty", 30m, 60m, 0, 20);
        await cache.PriceFilteredPageAsync("beauty", 30m, 60m, 0, 20);

        Assert.Single(source.Queries);
    }

    [Fact]
    public async Task BoundsWrittenAtDifferentScalesShareOneEntry()
    {
        var source = new PriceCapableSource(Catalog());
        var cache = CacheOver(source);

        await cache.PriceFilteredPageAsync(null, 30m, 60m, 0, 20);
        await cache.PriceFilteredPageAsync(null, 30.00m, 60.0m, 0, 20);

        // Equal as decimals, distinct as strings — the key must collapse them or one question becomes
        // two entries and two source calls.
        Assert.Single(source.Queries);
    }

    /// <summary>
    /// The cost of push-down, asserted rather than left to a comment: on this path the bounds are in
    /// the cache key, so varying them does produce distinct calls. That is correct — the answers
    /// genuinely differ — and it is the trade B2 refused for DummyJSON, where each such call was a
    /// full-catalog download rather than one indexed query.
    /// </summary>
    [Fact]
    public async Task VaryingBoundsProduceDistinctCallsOnThePushedDownPath()
    {
        var source = new PriceCapableSource(Catalog());
        var cache = CacheOver(source);

        for (int cents = 0; cents < 20; cents++)
        {
            await cache.PriceFilteredPageAsync(null, 10m + (cents * 0.01m), 50m, 0, 20);
        }

        Assert.Equal(20, source.Queries.Count);
    }

    /// <summary>
    /// A source that declares the capability and then does not honour it has handed back a page that
    /// was paginated before filtering. Neither the items nor the total can be repaired after the fact,
    /// so the answer is refused rather than served with a 200.
    /// </summary>
    [Fact]
    public async Task ASourceThatBreaksItsOwnCapabilityPromiseIsRefused()
    {
        var source = new PriceCapableSource(Catalog(), honourPriceFilter: false);

        var refused = await Assert.ThrowsAsync<UpstreamException>(
            async () => await CacheOver(source).PriceFilteredPageAsync(null, 30m, 60m, 0, 20));

        Assert.Contains("did not apply the price filter", refused.Message);
    }

    /// <summary>
    /// <c>MaxInMemoryCandidates</c> guards an in-memory materialization that this path never performs,
    /// so a catalog far over the threshold is served without complaint. S5's refusal is about holding a
    /// filtered set in process, not about catalog size as such.
    /// </summary>
    [Fact]
    public async Task TheInMemoryThresholdDoesNotApplyWhenNothingIsMaterializedInMemory()
    {
        var source = new PriceCapableSource(Catalog());

        var page = await CacheOver(source, threshold: 1).PriceFilteredPageAsync(null, 10m, 100m, 0, 20);

        Assert.Equal(10, page.Total);
    }
}
