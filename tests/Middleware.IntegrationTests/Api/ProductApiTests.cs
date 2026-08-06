using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Middleware.Core.Domain;
using Middleware.Core.Exceptions;
using Middleware.IntegrationTests.Support;
using NSubstitute;
using NSubstitute.ClearExtensions;
using NSubstitute.ExceptionExtensions;

namespace Middleware.IntegrationTests.Api;

/// <summary>
/// End-to-end integration test of the product API, ported from <c>ProductApiIT</c>: real endpoints,
/// service, cache, security pipeline, problem handler and SQLite-backed user store wired together via
/// <see cref="MiddlewareApiFactory"/>. Only the network boundary (the product source) is substituted,
/// so the JWT auth flow, the RFC-7807 error contract and request validation are all exercised as a
/// client would hit them.
/// </summary>
public sealed class ProductApiTests(MiddlewareApiFactory factory)
    : IClassFixture<MiddlewareApiFactory>, IAsyncLifetime
{
    private const string Username = "tester";
    private const string Password = "pass1234";
    private const string ProblemJson = "application/problem+json";

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        factory.Source.ClearSubstitute(ClearOptions.All);
        // Clearing the substitute is only half of it now that the listing and category endpoints are
        // cached: a warm entry answers without the substitute being consulted at all.
        await factory.ClearCachesAsync();
        await factory.ResetUsersAsync(Username, Password);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // --- authentication ----------------------------------------------------

    [Fact]
    public async Task LoginWithValidCredentialsReturnsToken()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new { username = Username, password = Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        Assert.Equal(3600, body.GetProperty("expiresInSeconds").GetInt64());
    }

    [Fact]
    public async Task LoginWithBadPasswordReturns401ProblemDetail()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new { username = Username, password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        // Generic detail: the specific cause must not leak (no account enumeration).
        var body = await ReadJsonAsync(response);
        Assert.Equal("Invalid username or password.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ProtectedEndpointWithoutTokenReturns401()
    {
        var response = await _client.GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ProtectedEndpointWithInvalidTokenReturns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- product endpoints (authenticated) ---------------------------------

    [Fact]
    public async Task ListReturnsPagedSummariesWhenAuthenticated()
    {
        List<Product> items =
        [
            TestData.Product(1, "Phone", "A phone", 9.99m, "smartphones"),
            TestData.Product(2, "Laptop", "A laptop", 999m, "laptops")
        ];
        factory.Source.ListAsync(0, 20, Arg.Any<CancellationToken>()).Returns(new ProductPage(items, 2, 0, 20));

        var response = await GetAuthenticatedAsync("/api/products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(2, body.GetProperty("items").GetArrayLength());
        Assert.Equal("Phone", body.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(2, body.GetProperty("totalItems").GetInt64());
    }

    [Fact]
    public async Task GetByIdReturnsFullDetail()
    {
        factory.Source.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(TestData.Product(1, "Phone", "A phone", 9.99m, "smartphones"));

        var response = await GetAuthenticatedAsync("/api/products/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(1, body.GetProperty("id").GetInt64());
        Assert.Equal("Phone", body.GetProperty("name").GetString());
        Assert.Equal("smartphones", body.GetProperty("category").GetString());
    }

    [Fact]
    public async Task GetByMissingIdReturns404ProblemDetail()
    {
        factory.Source.GetByIdAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProductNotFoundException(999));

        var response = await GetAuthenticatedAsync("/api/products/999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var body = await ReadJsonAsync(response);
        Assert.Equal("https://abysalto.middleware/errors/product-not-found", body.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("timestamp").GetString()));
    }

    [Fact]
    public async Task GetByNonPositiveIdReturns400()
    {
        var response = await GetAuthenticatedAsync("/api/products/-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task FilterWithMinGreaterThanMaxReturns400()
    {
        var response = await GetAuthenticatedAsync("/api/products/filter?minPrice=100&maxPrice=10");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("minPrice must not be greater than maxPrice", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task SearchWithoutQueryReturns400()
    {
        var response = await GetAuthenticatedAsync("/api/products/search");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task UpstreamFailureIsReportedAs502()
    {
        factory.Source.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new UpstreamException("boom"));

        var response = await GetAuthenticatedAsync("/api/products");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var body = await ReadJsonAsync(response);
        Assert.Equal("https://abysalto.middleware/errors/upstream-error", body.GetProperty("type").GetString());
    }

    // --- helpers -----------------------------------------------------------

    private async Task<string> LoginAsync(string username, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("token").GetString()!;
    }

    private async Task<HttpResponseMessage> GetAuthenticatedAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(Username, Password));
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
