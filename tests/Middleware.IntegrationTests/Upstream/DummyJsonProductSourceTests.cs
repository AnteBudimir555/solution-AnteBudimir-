using Middleware.Core.Abstractions;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Middleware.Core.Domain;
using Middleware.Core.Exceptions;
using Middleware.Infrastructure.Upstream;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Middleware.IntegrationTests.Upstream;

/// <summary>
/// Component tests for <see cref="DummyJsonProductSource"/> against a mocked upstream (WireMock.Net).
/// The DummyJSON DTO deserialization, the domain mapping and the error-translation policy are all
/// exercised over real HTTP against a local stub server, without touching the internet.
/// </summary>
public sealed class DummyJsonProductSourceTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly DummyJsonProductSource _source;

    public DummyJsonProductSourceTests()
    {
        _server = WireMockServer.Start();
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!.TrimEnd('/') + "/") };
        _source = new DummyJsonProductSource(http, NullLogger<DummyJsonProductSource>.Instance);
    }

    private static string ProductJson(long id, string title, double price) => $$"""
        {
          "id": {{id}},
          "title": "{{title}}",
          "description": "A description",
          "category": "smartphones",
          "price": {{price.ToString(CultureInfo.InvariantCulture)}},
          "rating": 4.5,
          "stock": 10,
          "reviews": [
            {"rating":5,"comment":"Great","date":"2024-01-01","reviewerName":"Alice","reviewerEmail":"alice@example.com"}
          ],
          "dimensions": {"width":1.0,"height":2.0,"depth":3.0},
          "meta": {"createdAt":"c","updatedAt":"u","barcode":"b","qrCode":"q"}
        }
        """;

    private static string ListJson(params string[] products) =>
        $$"""{"products":[{{string.Join(",", products)}}],"total":{{products.Length}},"skip":0,"limit":30}""";

    private void StubJson(string path, string body) =>
        _server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(body));

    private void StubStatus(string path, int statusCode) =>
        _server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(statusCode));

    [Fact]
    public async Task ListMapsProductsAndPaginationMetadata()
    {
        StubJson("/products", ListJson(ProductJson(1, "Phone", 9.99)));

        var result = await _source.QueryAsync(new ProductQuery { Skip = 0, Limit = 30 });

        Assert.Single(result.Page.Items);
        Assert.Equal(1, result.Page.Total);
        Assert.Equal("Phone", result.Page.Items[0].Title);
        // DummyJSON has no price parameter, so every result says so.
        Assert.False(result.PriceFilterApplied);
    }

    [Fact]
    public async Task ListReturnsEmptyPageWhenProductsFieldAbsent()
    {
        StubJson("/products", """{"total":0,"skip":0,"limit":30}""");

        var result = await _source.QueryAsync(new ProductQuery { Skip = 0, Limit = 30 });

        Assert.Empty(result.Page.Items);
        Assert.Equal(0, result.Page.Total);
    }

    [Fact]
    public async Task GetByIdMapsPriceToDecimalAndDropsReviewerEmail()
    {
        StubJson("/products/1", ProductJson(1, "Phone", 9.99));

        var product = await _source.GetByIdAsync(1);

        Assert.Equal(9.99m, product.Price);
        Assert.NotNull(product.Reviews);
        Assert.Single(product.Reviews!);
        // The domain Review type has no email field: upstream PII is structurally dropped.
        Assert.Equal("Alice", product.Reviews![0].ReviewerName);
    }

    [Fact]
    public async Task GetByIdTranslates404ToProductNotFound()
    {
        StubStatus("/products/999", 404);

        await Assert.ThrowsAsync<ProductNotFoundException>(() => _source.GetByIdAsync(999));
    }

    [Fact]
    public async Task GetByIdTranslatesServerErrorToUpstreamException()
    {
        StubStatus("/products/1", 500);

        await Assert.ThrowsAsync<UpstreamException>(() => _source.GetByIdAsync(1));
    }

    [Fact]
    public async Task FindByCategoryQueriesCategoryPathAndMaps()
    {
        StubJson("/products/category/smartphones", ListJson(ProductJson(1, "Phone", 9.99)));

        var result = await _source.QueryAsync(new ProductQuery { Category = "smartphones", Skip = 0, Limit = 30 });

        Assert.Equal(["Phone"], result.Page.Items.Select(p => p.Title));
    }

    [Fact]
    public async Task SearchByNameQueriesSearchPathAndMaps()
    {
        StubJson("/products/search", ListJson(ProductJson(1, "Phone", 9.99)));

        var result = await _source.QueryAsync(new ProductQuery { NameContains = "phone", Skip = 0, Limit = 30 });

        Assert.Single(result.Page.Items);
    }

    [Fact]
    public async Task CategoriesReturnsListFromUpstream()
    {
        StubJson("/products/category-list", """["beauty","laptops"]""");

        Assert.Equal(["beauty", "laptops"], await _source.CategoriesAsync());
    }

    [Fact]
    public async Task ListTranslatesUpstreamErrorToUpstreamException()
    {
        StubStatus("/products", 503);

        await Assert.ThrowsAsync<UpstreamException>(
            () => _source.QueryAsync(new ProductQuery { Skip = 0, Limit = 30 }));
    }

    // --- routing and limit translation, which the query object moved into this adapter ---

    [Fact]
    public async Task AQueryWithNoFilterGoesToThePlainListPath()
    {
        StubJson("/products", ListJson(ProductJson(1, "Phone", 9.99)));

        await _source.QueryAsync(new ProductQuery { Skip = 10, Limit = 30 });

        var request = Assert.Single(_server.LogEntries).RequestMessage;
        Assert.Equal("/products", request?.Path);
        Assert.Equal("30", QueryParam(request, "limit"));
        Assert.Equal("10", QueryParam(request, "skip"));
    }

    /// <summary>
    /// "Everything" is null in the abstraction and limit=0 on the wire. Keeping the translation here is
    /// the point of removing IProductSource.All: the sentinel was DummyJSON's convention, and no other
    /// source has a reason to share it.
    /// </summary>
    [Fact]
    public async Task AnAbsentLimitBecomesTheUpstreamAllConvention()
    {
        StubJson("/products", ListJson(ProductJson(1, "Phone", 9.99)));

        await _source.QueryAsync(new ProductQuery { Limit = null });

        var request = Assert.Single(_server.LogEntries).RequestMessage;
        Assert.Equal("0", QueryParam(request, "limit"));
    }

    /// <summary>
    /// Under the old shape this was the same request as "give me everything", because limit=0 was the
    /// all-items sentinel. A caller that computed a page size of zero therefore downloaded the entire
    /// catalog. It is now an error, and nothing reaches the upstream.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ANonPositiveLimitIsRejectedRatherThanFetchingEverything(int limit)
    {
        StubJson("/products", ListJson(ProductJson(1, "Phone", 9.99)));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _source.QueryAsync(new ProductQuery { Limit = limit }));

        Assert.Empty(_server.LogEntries);
    }

    /// <summary>
    /// The query object can express an intersection DummyJSON has no endpoint for. Refusing beats
    /// serving one filter and dropping the other, which would return a superset the caller believes is
    /// exact.
    /// </summary>
    [Fact]
    public async Task CombiningCategoryAndNameIsRefusedRatherThanSilentlyDroppingOne()
    {
        var refused = await Assert.ThrowsAsync<NotSupportedException>(
            () => _source.QueryAsync(new ProductQuery { Category = "beauty", NameContains = "phone", Limit = 30 }));

        Assert.Contains("category and name", refused.Message);
        Assert.Empty(_server.LogEntries);
    }

    /// <summary>
    /// Price bounds are accepted and ignored — DummyJSON cannot apply them — but the result says so, so
    /// the caller knows it still has to filter.
    /// </summary>
    [Fact]
    public async Task PriceBoundsAreReportedAsNotAppliedRatherThanSilentlyHonoured()
    {
        StubJson("/products", ListJson(ProductJson(1, "Phone", 9.99), ProductJson(2, "Laptop", 999)));

        var result = await _source.QueryAsync(new ProductQuery { MinPrice = 500m, Limit = 30 });

        Assert.False(_source.SupportsPriceFilter);
        Assert.False(result.PriceFilterApplied);
        // Both products come back: the bound was not applied anywhere.
        Assert.Equal(2, result.Page.Items.Count);
    }

    private static string? QueryParam(WireMock.IRequestMessage? request, string name) =>
        request?.Query is { } query && query.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    public void Dispose() => _server.Dispose();
}
