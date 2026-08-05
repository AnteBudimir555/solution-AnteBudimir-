using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Middleware.Core.Abstractions;
using Middleware.Core.Domain;
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
/// in <c>ProductQueryCacheSingleFlightTests</c>.
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
    }

    private ProductQueryCache Cache() => WithThreshold(5000);

    private ProductQueryCache WithThreshold(int threshold) =>
        new(_source, _cache, Options.Create(new UpstreamOptions { MaxInMemoryCandidates = threshold }),
            NullLogger<ProductQueryCache>.Instance);

    [Fact]
    public async Task SearchNormalizesQueryAndTranslatesOffset()
    {
        var page = new ProductPage([], 0, 20, 10);
        _source.SearchByNameAsync("phone", 20, 10, Arg.Any<CancellationToken>()).Returns(page);

        var result = await Cache().SearchAsync("  Phone ", 2, 10);

        Assert.Equal(20, result.Skip);
        Assert.Equal(10, result.Limit);
        await _source.Received(1).SearchByNameAsync("phone", 20, 10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CategoryPageWithBlankCategoryFallsBackToFullList()
    {
        var page = new ProductPage([], 0, 0, 20);
        _source.ListAsync(0, 20, Arg.Any<CancellationToken>()).Returns(page);

        var result = await Cache().CategoryPageAsync("   ", 0, 20);

        Assert.Equal(20, result.Limit);
        await _source.Received(1).ListAsync(0, 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CategoryPageWithCategoryQueriesThatCategory()
    {
        var page = new ProductPage([], 0, 10, 10);
        _source.FindByCategoryAsync("beauty", 10, 10, Arg.Any<CancellationToken>()).Returns(page);

        var result = await Cache().CategoryPageAsync("Beauty", 1, 10);

        Assert.Equal(10, result.Skip);
        await _source.Received(1).FindByCategoryAsync("beauty", 10, 10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PriceFilteredCandidatesKeepsOnlyPricesWithinBoundsAndDropsNullPrices()
    {
        var catalog = new List<Product>
        {
            TestData.Product(1, 5m),
            TestData.Product(2, 10m),
            TestData.Product(3, 30m),
            TestData.Product(4, 50m),
            TestData.Product(5, 80m),
            TestData.Product(6, null)   // must be excluded, never throw
        };
        _source.ListAsync(0, IProductSource.All, Arg.Any<CancellationToken>())
            .Returns(new ProductPage(catalog, catalog.Count, 0, 0));

        var filtered = await WithThreshold(5000).PriceFilteredCandidatesAsync(null, 10m, 50m);

        Assert.Equal(new long[] { 2, 3, 4 }, filtered.Select(p => p.Id));
    }

    [Fact]
    public async Task PriceFilteredCandidatesTreatsBoundsAsInclusiveAndOpenEnded()
    {
        var catalog = new List<Product>
        {
            TestData.Product(1, 10m),
            TestData.Product(2, 100m),
            TestData.Product(3, 1000m)
        };
        _source.FindByCategoryAsync("beauty", 0, IProductSource.All, Arg.Any<CancellationToken>())
            .Returns(new ProductPage(catalog, catalog.Count, 0, 0));

        // Only a lower bound -> everything >= 100 (inclusive).
        var minOnly = await WithThreshold(5000).PriceFilteredCandidatesAsync("Beauty", 100m, null);

        Assert.Equal(new long[] { 2, 3 }, minOnly.Select(p => p.Id));
    }

    [Fact]
    public async Task PriceFilteredCandidatesStillFiltersWhenOverInMemoryThreshold()
    {
        var catalog = new List<Product> { TestData.Product(1, 5m), TestData.Product(2, 50m) };
        _source.ListAsync(0, IProductSource.All, Arg.Any<CancellationToken>())
            .Returns(new ProductPage(catalog, catalog.Count, 0, 0));

        // threshold 1 forces the warn branch; behaviour must be unchanged.
        var filtered = await WithThreshold(1).PriceFilteredCandidatesAsync(null, null, 10m);

        Assert.Equal(new long[] { 1 }, filtered.Select(p => p.Id));
    }
}
