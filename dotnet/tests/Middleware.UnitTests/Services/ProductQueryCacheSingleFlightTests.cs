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
            NullLogger<ProductQueryCache>.Instance);
    }

    [Fact]
    public async Task ConcurrentIdenticalSearchesShareOneUpstreamFetch()
    {
        var calls = 0;
        var page = new ProductPage([], 0, 0, 20);
        _source.SearchByNameAsync("phone", 0, 20, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(100); // hold the factory open so the other callers pile up behind it
                return page;
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
        var page = new ProductPage([], 0, 0, 20);
        _source.SearchByNameAsync("phone", 0, 20, Arg.Any<CancellationToken>()).Returns(page);

        await _cache.SearchAsync("phone", 0, 20);
        await _cache.SearchAsync("phone", 0, 20);

        await _source.Received(1).SearchByNameAsync("phone", 0, 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PriceFilteredCandidatesAreCachedIndependentlyOfPage()
    {
        // The candidate set is keyed by category + price bounds only, so paging through a filtered
        // result must reuse a single upstream catalog fetch regardless of how many pages are requested.
        var catalog = new ProductPage(
            [new Product(1, "A", null, "beauty", 10m, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null, null, null)],
            1, 0, 0);
        _source.FindByCategoryAsync("beauty", 0, IProductSource.All, Arg.Any<CancellationToken>()).Returns(catalog);

        // Two different "pages" of the same filter (same category + bounds) ...
        await _cache.PriceFilteredCandidatesAsync("beauty", 5m, 50m);
        await _cache.PriceFilteredCandidatesAsync("beauty", 5m, 50m);

        // ... hit the upstream exactly once.
        await _source.Received(1).FindByCategoryAsync("beauty", 0, IProductSource.All, Arg.Any<CancellationToken>());
    }
}
