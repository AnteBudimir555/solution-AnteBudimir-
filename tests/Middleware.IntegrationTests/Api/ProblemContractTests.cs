using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Middleware.Core.Exceptions;
using Middleware.Core.Options;
using Middleware.Infrastructure.Security;
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

    /// <summary>
    /// The detail every failed *token* check renders (S6). Distinct from the login endpoint's
    /// "Invalid username or password.", which stays generic to prevent account enumeration —
    /// see <c>ProductApiTests.LoginWithBadPasswordReturns401ProblemDetail</c>.
    /// </summary>
    private const string ChallengeDetail = "Missing or invalid bearer token.";

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
        // Not the login endpoint's "Invalid username or password." — see ChallengeDetail.
        Assert.Equal(ChallengeDetail, body.GetProperty("detail").GetString());
    }

    /// <summary>
    /// MIGRATION_PLAN S6. Every way a bearer token can be refused renders one body: the challenge
    /// handler must not become a place where the failure reason leaks out through the wording. The
    /// cases are the ones the handler's own comment claims to cover, asserted rather than assumed —
    /// a malformed token and a well-formed one signed by a stranger take different paths through the
    /// JWT middleware, and expiry is a validation failure rather than a parse failure.
    /// </summary>
    [Theory]
    [InlineData("not-a-real-token")]        // unparseable
    [InlineData("")]                        // the header is present but carries nothing
    public async Task AnUnusableBearerHeaderProducesTheSameUnauthorizedProblem(string token)
    {
        AssertChallengeProblem(await SendWithTokenAsync(token));
    }

    [Fact]
    public async Task ATokenSignedByAStrangerProducesTheSameUnauthorizedProblem()
    {
        var forged = TokenIssuer("a-different-secret-of-at-least-thirty-two-characters", 60).IssueToken(Username);

        AssertChallengeProblem(await SendWithTokenAsync(forged));
    }

    [Fact]
    public async Task AnExpiredTokenProducesTheSameUnauthorizedProblem()
    {
        // Validation runs with zero clock skew, so a token that expired a minute ago is already dead.
        var expired = TokenIssuer(MiddlewareApiFactory.JwtSecret, -1).IssueToken(Username);

        AssertChallengeProblem(await SendWithTokenAsync(expired));
    }

    /// <summary>
    /// The one cause that is neither missing nor invalid as a token: the signature, issuer and lifetime
    /// all check out, and <c>OnTokenValidated</c> rejects it because the subject no longer exists. It
    /// reaches the same challenge handler, which is what lets a single wording cover the path.
    /// </summary>
    [Fact]
    public async Task ATokenForADeletedUserProducesTheSameUnauthorizedProblem()
    {
        var token = await LoginAsync();
        await factory.ResetUsersAsync("someone-else", Password);

        AssertChallengeProblem(await SendWithTokenAsync(token));
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

    /// <summary>Asserts the single body every rejected-token path must produce.</summary>
    private static void AssertChallengeProblem(JsonElement body)
    {
        AssertProblemShape(body, 401, "unauthorized");
        Assert.Equal("Authentication failed", body.GetProperty("title").GetString());
        Assert.Equal(ChallengeDetail, body.GetProperty("detail").GetString());
    }

    /// <summary>
    /// Sends the header verbatim rather than through <see cref="AuthenticationHeaderValue"/>, which
    /// rejects an empty parameter client-side — the point of these cases is what the *server* does
    /// with a header a real client can send.
    /// </summary>
    private async Task<JsonElement> SendWithTokenAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await ReadJsonAsync(response);
    }

    /// <summary>
    /// Mints tokens the host will refuse. A negative lifetime is what makes the expired case
    /// constructible without waiting — <see cref="JwtService"/> writes <c>exp</c> explicitly rather
    /// than letting the handler default it.
    /// </summary>
    private static JwtService TokenIssuer(string secret, long expirationMinutes) =>
        new(new JwtOptions
        {
            Secret = secret,
            Issuer = MiddlewareApiFactory.JwtIssuer,
            ExpirationMinutes = expirationMinutes
        });

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
