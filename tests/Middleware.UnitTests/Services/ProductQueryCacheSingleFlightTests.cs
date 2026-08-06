using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Middleware.Core.Abstractions;
using Middleware.Core.Domain;
using Middleware.Core.Options;
using Middleware.Core.Services;
using NSubstitute;

namespace Middleware.UnitTests.Services;

/// <summary>
/// Verifies the caching contract that <see cref="ProductQueryCache"/> relies on <see cref="HybridCache"/>
/// for — the .NET equivalent of Spring's <c>@Cacheable(sync = true)</c>:
/// <list type="bullet">
///   <item>concurrent calls for the same key collapse to a single upstream fetch (single-flight); and</item>
///   <item>a subsequent call for the same key is served from cache without touching the source.</item>
/// </list>
/// </summary>
public class ProductQueryCacheSingleFlightTests
{
    private readonly IProductSource _source = Substitute.For<IProductSource>();
    private readonly ProductQueryCache _cache;

    public ProductQueryCacheSingleFlightTests()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var hybrid = services.BuildServiceProvider().GetRequiredService<HybridCache>();
        _cache = new ProductQueryCache(_source, hybrid,
            Options.Create(new UpstreamOptions { MaxInMemoryCandidates = 5000 }),
            Options.Create(new CacheOptions()),
            NullLogger<ProductQueryCache>.Instance);
    }

    [Fact]
    public async Task ConcurrentIdenticalSearchesShareOneUpstreamFetch()
    {
        var calls = 0;
        var result = new ProductQueryResult(new ProductPage([], 0, 0, 20), false);
        _source.QueryAsync(SearchQuery, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(100); // hold the factory open so the other callers pile up behind it
                return result;
            });

        var tasks = Enumerable.Range(0, 25)
            .Select(_ => _cache.SearchAsync("phone", 0, 20).AsTask())
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SecondIdenticalSearchIsServedFromCache()
    {
        _source.QueryAsync(SearchQuery, Arg.Any<CancellationToken>())
            .Returns(new ProductQueryResult(new ProductPage([], 0, 0, 20), false));

        await _cache.SearchAsync("phone", 0, 20);
        await _cache.SearchAsync("phone", 0, 20);

        await _source.Received(1).QueryAsync(SearchQuery, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PriceFilteredCandidatesAreCachedIndependentlyOfPage()
    {
        // The candidate set is keyed by category alone, so paging through a filtered result must reuse
        // a single upstream catalog fetch regardless of how many pages are requested.
        var catalog = new ProductPage(
            [new Product(1, "A", null, "beauty", 10m, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null, null, null)],
            1, 0, 0);
        _source.QueryAsync(CandidateQuery, Arg.Any<CancellationToken>())
            .Returns(new ProductQueryResult(catalog, false));

        // Two different pages of the same filter (same category + bounds) ...
        await _cache.PriceFilteredPageAsync("beauty", 5m, 50m, 0, 20);
        await _cache.PriceFilteredPageAsync("beauty", 5m, 50m, 1, 20);

        // ... hit the upstream exactly once.
        await _source.Received(1).QueryAsync(CandidateQuery, Arg.Any<CancellationToken>());
    }

    private static readonly ProductQuery SearchQuery =
        new() { NameContains = "phone", Skip = 0, Limit = 20 };

    /// <summary>The unbounded, unfiltered fetch the in-memory price path issues for one category.</summary>
    private static readonly ProductQuery CandidateQuery =
        new() { Category = "beauty", Limit = null };
}
