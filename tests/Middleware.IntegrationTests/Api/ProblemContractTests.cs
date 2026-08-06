using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Middleware.Core.Exceptions;
using NSubstitute;
using NSubstitute.ClearExtensions;
using NSubstitute.ExceptionExtensions;

namespace Middleware.IntegrationTests.Api;

/// <summary>
/// Golden-file style assertions on the RFC-7807 contract (risk R2): every failure — validation, type
/// mismatch, auth, unknown route, unexpected error — renders the same member set with this API's
/// <c>type</c> namespace and <c>timestamp</c> property.
/// </summary>
public sealed class ProblemContractTests(MiddlewareApiFactory factory)
    : IClassFixture<MiddlewareApiFactory>, IAsyncLifetime
{
    private const string Username = "tester";
    private const string Password = "pass1234";
    private const string TypePrefix = "https://abysalto.middleware/errors/";

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

    [Fact]
    public async Task ValidationFailuresJoinEveryConstraintMessage()
    {
        var body = await GetProblemAsync("/api/products?page=-1&size=0", HttpStatusCode.BadRequest);

        AssertProblemShape(body, 400, "validation-failed");
        Assert.Equal("Validation failed", body.GetProperty("title").GetString());
        // Each message carries Spring's "<controllerMethod>.<parameter>" property path, because that is
        // what the Java service's ConstraintViolationException joins into the detail.
        Assert.Equal("list.page: must be greater than or equal to 0; list.size: must be greater than or equal to 1",
            body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task BlankSearchQueryIsAValidationFailure()
    {
        var body = await GetProblemAsync("/api/products/search?q=%20%20", HttpStatusCode.BadRequest);

        AssertProblemShape(body, 400, "validation-failed");
        Assert.Equal("search.q: must not be blank", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task NonNumericParameterIsReportedByNameWithoutLeakingInternalTypes()
    {
        var body = await GetProblemAsync("/api/products?page=abc", HttpStatusCode.BadRequest);

        AssertProblemShape(body, 400, "bad-request");
        Assert.Equal("Invalid request", body.GetProperty("title").GetString());
        Assert.Equal("Parameter 'page' has an invalid value.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task LoginBodyValidationReportsFieldNames()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new { username = "", password = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonAsync(response);
        AssertProblemShape(body, 400, "validation-failed");
        Assert.Equal("username: must not be blank; password: must not be blank",
            body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task MissingTokenProducesTheUnauthorizedProblem()
    {
        var response = await _client.GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await ReadJsonAsync(response);
        AssertProblemShape(body, 401, "unauthorized");
        Assert.Equal("Authentication failed", body.GetProperty("title").GetString());
        Assert.Equal("Invalid username or password.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task UnknownRouteProducesTheNotFoundProblem()
    {
        var body = await GetProblemAsync("/api/does-not-exist", HttpStatusCode.NotFound);

        AssertProblemShape(body, 404, "not-found");
    }

    [Fact]
    public async Task UnsupportedMethodProducesAProblemBody()
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/products/1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        AssertProblemShape(await ReadJsonAsync(response), 405, "method-not-allowed");
    }

    [Fact]
    public async Task UnexpectedFailureIsReportedAsAGeneric500()
    {
        factory.Source.CategoriesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("internal detail that must not leak"));

        var body = await GetProblemAsync("/api/products/categories", HttpStatusCode.InternalServerError);

        AssertProblemShape(body, 500, "internal-error");
        Assert.Equal("An unexpected error occurred.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task UpstreamFailureDetailNeverLeaksTheUpstreamMessage()
    {
        factory.Source.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new UpstreamException("connection refused to dummyjson"));

        var body = await GetProblemAsync("/api/products", HttpStatusCode.BadGateway);

        AssertProblemShape(body, 502, "upstream-error");
        Assert.Equal("The product source is currently unavailable.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task SuccessfulResponsesAreUnaffectedByTheProblemPipeline()
    {
        IReadOnlyList<string> categories = ["smartphones", "laptops"];
        factory.Source.CategoriesAsync(Arg.Any<CancellationToken>()).Returns(categories);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/products/categories");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync());
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(2, (await ReadJsonAsync(response)).GetArrayLength());
    }

    // --- helpers -----------------------------------------------------------

    private static void AssertProblemShape(JsonElement body, int status, string typeSlug)
    {
        Assert.Equal(["type", "title", "status", "detail", "instance", "timestamp"],
            body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(TypePrefix + typeSlug, body.GetProperty("type").GetString());
        Assert.Equal(status, body.GetProperty("status").GetInt32());
        Assert.True(DateTimeOffset.TryParse(body.GetProperty("timestamp").GetString(), out _));
    }

    private async Task<string> LoginAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new { username = Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("token").GetString()!;
    }

    private async Task<JsonElement> GetProblemAsync(string url, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync());

        var response = await _client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
