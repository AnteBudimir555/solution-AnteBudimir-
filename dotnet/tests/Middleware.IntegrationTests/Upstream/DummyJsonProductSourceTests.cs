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

        var page = await _source.ListAsync(0, 30);

        Assert.Single(page.Items);
        Assert.Equal(1, page.Total);
        Assert.Equal("Phone", page.Items[0].Title);
    }

    [Fact]
    public async Task ListReturnsEmptyPageWhenProductsFieldAbsent()
    {
        StubJson("/products", """{"total":0,"skip":0,"limit":30}""");

        var page = await _source.ListAsync(0, 30);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
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

        var page = await _source.FindByCategoryAsync("smartphones", 0, 30);

        Assert.Equal(["Phone"], page.Items.Select(p => p.Title));
    }

    [Fact]
    public async Task SearchByNameQueriesSearchPathAndMaps()
    {
        StubJson("/products/search", ListJson(ProductJson(1, "Phone", 9.99)));

        var page = await _source.SearchByNameAsync("phone", 0, 30);

        Assert.Single(page.Items);
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

        await Assert.ThrowsAsync<UpstreamException>(() => _source.ListAsync(0, 30));
    }

    public void Dispose() => _server.Dispose();
}
