using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Middleware.IntegrationTests.Support;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace Middleware.IntegrationTests.Api;

/// <summary>
/// Byte-level parity with the Java service on details the API-shadowing harness found, and that no
/// ported test covered because the Java suite never asserted them either — they are Spring behaviours
/// the Java tests inherited for free.
///
/// <para>The harness proves these against a live Java service; these tests keep them from regressing
/// without one running, which is what makes them worth having in-process.</para>
/// </summary>
public sealed class WireParityTests(MiddlewareApiFactory factory)
    : IClassFixture<MiddlewareApiFactory>, IAsyncLifetime
{
    private const string Username = "tester";
    private const string Password = "pass1234";

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        factory.Source.ClearSubstitute(ClearOptions.All);
        await factory.ResetUsersAsync(Username, Password);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Jackson writes every <c>Double</c> with a decimal point, so a whole value goes out as
    /// <c>10.0</c>. System.Text.Json would write <c>10</c> — the same number, different bytes, and a
    /// visible difference to any client comparing payloads.
    /// </summary>
    [Fact]
    public async Task WholeNumberDoublesKeepJavasTrailingDecimalPoint()
    {
        factory.Source.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(TestData.Product(1, "Phone", "A phone", 9.99m, "smartphones"));

        var body = await GetRawAsync("/api/products/1");

        Assert.Contains("\"discountPercentage\":10.0", body, StringComparison.Ordinal);
        Assert.Contains("\"width\":1.0", body, StringComparison.Ordinal);
        // Values that already carry a fraction are untouched, and decimal prices keep their own scale.
        Assert.Contains("\"rating\":4.5", body, StringComparison.Ordinal);
        Assert.Contains("\"price\":9.99", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wording is Spring's <c>NoResourceFoundException</c> message. It is part of the published
    /// contract, so the port restates it rather than substituting a message of its own.
    /// </summary>
    [Fact]
    public async Task UnknownPathCarriesSpringsNotFoundDetail()
    {
        var body = await GetProblemAsync("/api/products/does/not/exist", HttpStatusCode.NotFound);

        Assert.Equal("No static resource api/products/does/not/exist.", body.GetProperty("detail").GetString());
    }

    /// <summary>
    /// ASP.NET Core's matcher normalizes a trailing slash and would serve the list; Spring matches
    /// nothing and answers 404 — naming the path without the slash, though <c>instance</c> keeps it.
    /// </summary>
    [Fact]
    public async Task TrailingSlashOnAnApiPathIsNotFound()
    {
        var body = await GetProblemAsync("/api/products/", HttpStatusCode.NotFound);

        Assert.Equal("No static resource api/products.", body.GetProperty("detail").GetString());
        Assert.Equal("/api/products/", body.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task UnsupportedMethodDetailNamesTheMethod()
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/products/1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("Method 'DELETE' is not supported.", body.GetProperty("detail").GetString());
    }

    /// <summary>
    /// A wrong method on the public auth path must answer 405, not 401: Spring permits the path
    /// outright, so the request reaches handler resolution. Without the routing-parity step the
    /// fallback authorization policy claims the framework's method-mismatch endpoint and challenges.
    /// </summary>
    [Fact]
    public async Task MethodMismatchOnThePublicAuthPathIsNotAnAuthenticationFailure()
    {
        var response = await _client.GetAsync("/api/auth/login");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("Method 'GET' is not supported.", body.GetProperty("detail").GetString());
    }

    /// <summary>
    /// A body the framework cannot bind is reported by the problem handler, not by the bare status.
    /// Minimal APIs only throw on an unreadable body in Development by default, so without the
    /// explicit setting this answers one way in dev and another in production — the divergence the
    /// shadowing run found once the harness was pointed at the packaged image. This factory hosts
    /// under Staging, so it exercises the non-Development path.
    /// </summary>
    [Fact]
    public async Task UnreadableRequestBodyIsReportedByTheProblemHandler()
    {
        using var content = new StringContent("""{"username":"demo",,}""", Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/auth/login", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("Failed to read request", body.GetProperty("detail").GetString());
    }

    /// <summary>
    /// The counterpart: on a protected path the token check still comes first, exactly as the Java
    /// security filter chain does by running ahead of handler resolution.
    /// </summary>
    [Fact]
    public async Task MethodMismatchOnAProtectedPathStillRequiresAToken()
    {
        var response = await _client.PostAsync("/api/products", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- helpers -----------------------------------------------------------

    private async Task<string> LoginAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new { username = Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("token").GetString()!;
    }

    private async Task<string> GetRawAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
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
