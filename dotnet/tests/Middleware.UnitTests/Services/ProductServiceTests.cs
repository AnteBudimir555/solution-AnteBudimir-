using Microsoft.Extensions.Options;
using Middleware.Core.Abstractions;
using Middleware.Core.Domain;
using Middleware.Core.Dtos;
using Middleware.Core.Options;
using Middleware.Core.Services;
using Middleware.UnitTests.Support;
using NSubstitute;

namespace Middleware.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ProductService"/> orchestration: offset computation, DTO mapping, the
/// no-price vs price-filter routing and the in-service pagination slice (including out-of-range pages).
/// A real <see cref="ProductMapper"/> is used so mapping is exercised end-to-end; the cache and source
/// boundaries are substituted.
/// </summary>
public class ProductServiceTests
{
    private readonly IProductSource _source = Substitute.For<IProductSource>();
    private readonly IProductQueryCache _queries = Substitute.For<IProductQueryCache>();
    private readonly ProductMapper _mapper = new(Options.Create(new SummaryOptions { DescriptionMaxLength = 100 }));

    private ProductService Service() => new(_source, _queries, _mapper);

    [Fact]
    public async Task ListMapsPageToSummariesWithPaginationMetadata()
    {
        var items = new List<Product> { TestData.Product(1, 10m), TestData.Product(2, 20m) };
        _source.ListAsync(0, 20, Arg.Any<CancellationToken>()).Returns(new ProductPage(items, 42, 0, 20));

        var response = await Service().ListAsync(0, 20);

        Assert.Equal(2, response.Items.Count);
        Assert.Equal("Product 1", response.Items[0].Name);
        Assert.Equal(0, response.Page);
        Assert.Equal(20, response.Size);
        Assert.Equal(42, response.TotalItems);
        Assert.Equal(3, response.TotalPages);
    }

    [Fact]
    public async Task ListTranslatesPageIndexToOffset()
    {
        _source.ListAsync(40, 20, Arg.Any<CancellationToken>()).Returns(new ProductPage([], 100, 40, 20));

        await Service().ListAsync(2, 20);

        await _source.Received(1).ListAsync(40, 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByIdReturnsMappedDetail()
    {
        _source.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(TestData.Product(7, "Camera", "desc", 99m, "photo"));

        var detail = await Service().GetByIdAsync(7);

        Assert.Equal(7, detail.Id);
        Assert.Equal("Camera", detail.Name);
    }

    [Fact]
    public async Task FilterWithoutPriceBoundsDelegatesToCategoryPage()
    {
        var items = new List<Product> { TestData.Product(1, 10m) };
        _queries.CategoryPageAsync("beauty", 0, 20, Arg.Any<CancellationToken>())
            .Returns(new ProductPage(items, 1, 0, 20));

        var response = await Service().FilterAsync("beauty", null, null, 0, 20);

        Assert.Single(response.Items);
        Assert.Equal(1, response.TotalItems);
        await _queries.Received(1).CategoryPageAsync("beauty", 0, 20, Arg.Any<CancellationToken>());
        // The price-filter candidate path must not be touched when no bound is present.
        Assert.Empty(_source.ReceivedCalls());
    }

    [Fact]
    public async Task FilterWithPriceBoundsSlicesRequestedPageFromCandidateSet()
    {
        var candidates = Enumerable.Range(1, 5).Select(i => TestData.Product(i, i * 10m)).ToList();
        _queries.PriceFilteredCandidatesAsync("beauty", 10m, 50m, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Product>)candidates);

        var page0 = await Service().FilterAsync("beauty", 10m, 50m, 0, 2);

        Assert.Equal(2, page0.Items.Count);
        Assert.Equal(5, page0.TotalItems);
        Assert.Equal(3, page0.TotalPages);
    }

    [Fact]
    public async Task FilterWithPriceBoundsClampsPageBeyondTheEndToEmpty()
    {
        var candidates = new List<Product> { TestData.Product(1, 10m), TestData.Product(2, 20m) };
        _queries.PriceFilteredCandidatesAsync(null, 1m, null, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Product>)candidates);

        // page 5 of size 2 is well past the 2-item candidate set: no items, but total is preserved.
        var response = await Service().FilterAsync(null, 1m, null, 5, 2);

        Assert.Empty(response.Items);
        Assert.Equal(2, response.TotalItems);
    }

    [Fact]
    public async Task SearchDelegatesToCache()
    {
        _queries.SearchAsync("phone", 0, 20, Arg.Any<CancellationToken>())
            .Returns(new ProductPage([], 0, 0, 20));

        await Service().SearchByNameAsync("phone", 0, 20);

        await _queries.Received(1).SearchAsync("phone", 0, 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CategoriesDelegatesToSource()
    {
        _source.CategoriesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)new[] { "beauty", "laptops" });

        Assert.Equal(new[] { "beauty", "laptops" }, await Service().CategoriesAsync());
    }
}
