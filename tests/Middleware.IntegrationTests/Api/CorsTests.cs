using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NSubstitute;

namespace Middleware.IntegrationTests.Api;

/// <summary>
/// MIGRATION_PLAN S7. The API used to answer <c>Access-Control-Allow-Origin: *</c>; it now answers an
/// allowlist, empty by default. These run against the real host so the assertions cover the policy the
/// running middleware actually applies — the default-policy path through <c>app.UseCors()</c>, which
/// behaves differently from a named policy attached to an endpoint.
///
/// <para><strong>Read <see cref="ADisallowedOriginIsStillServedByTheEndpoint"/> before treating any of
/// this as an access control.</strong> It is not one.</para>
/// </summary>
public sealed class CorsTests
{
    private const string Allowed = "https://app.example.com";
    private const string AlsoAllowed = "https://admin.example.com";
    private const string Disallowed = "https://evil.example.com";
    private const string Username = "tester";
    private const string Password = "pass1234";

    [Fact]
    public async Task AnAllowedOriginIsEchoedBackAndNeverAsAWildcard()
    {
        using var factory = new MiddlewareApiFactory().WithAllowedOrigins(Allowed, AlsoAllowed);

        var response = await GetAsync(factory, Allowed);

        Assert.Equal(Allowed, Header(response, "Access-Control-Allow-Origin"));
        // A wildcard here would mean someone restored AllowAnyOrigin, which is the whole item.
        Assert.NotEqual("*", Header(response, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ADisallowedOriginReceivesNoAllowHeader()
    {
        using var factory = new MiddlewareApiFactory().WithAllowedOrigins(Allowed, AlsoAllowed);

        var response = await GetAsync(factory, Disallowed);

        Assert.Null(Header(response, "Access-Control-Allow-Origin"));
    }

    /// <summary>
    /// The default, and the one every other suite in this project runs under: no configured origin
    /// means no browser origin may read a response. A deployment that needs one names it.
    /// </summary>
    [Fact]
    public async Task WithNoConfiguredOriginsNoBrowserOriginIsAllowed()
    {
        using var factory = new MiddlewareApiFactory();

        var response = await GetAsync(factory, Allowed);

        Assert.Null(Header(response, "Access-Control-Allow-Origin"));
    }

    /// <summary>
    /// T7. <c>AllowAnyOrigin</c> and <c>AllowCredentials</c> are mutually exclusive — the framework
    /// throws when a policy asks for both — so the old configuration <em>could not</em> express the
    /// dangerous combination. Switching to <c>WithOrigins</c> removes that guardrail: measured, a
    /// <c>WithOrigins(...).AllowCredentials()</c> policy builds without complaint. This test is the
    /// replacement guardrail, and the reason it exists is that nothing else would notice.
    /// </summary>
    [Fact]
    public async Task CredentialsAreNeverAllowed()
    {
        using var factory = new MiddlewareApiFactory().WithAllowedOrigins(Allowed);

        var simple = await GetAsync(factory, Allowed);
        var preflight = await PreflightAsync(factory, Allowed);

        Assert.Null(Header(simple, "Access-Control-Allow-Credentials"));
        Assert.Null(Header(preflight, "Access-Control-Allow-Credentials"));
    }

    /// <summary>
    /// Deliberately preflights <c>/api/auth/login</c>, the one path where this could regress silently.
    /// <c>OPTIONS</c> matches no route there, so routing selects the framework's 405 short-circuit —
    /// and <c>UsePublicPathMethodMismatch</c> exists to <em>run</em> that short-circuit on public
    /// paths. With CORS placed after it (as it was), the preflight was answered 405 before CORS saw
    /// it, so a browser could never POST credentials cross-origin: the real request was never sent.
    /// Measured, then fixed by moving <c>UseCors</c> ahead of that step.
    /// </summary>
    [Fact]
    public async Task APreflightFromAnAllowedOriginAdvertisesTheRequestedMethod()
    {
        using var factory = new MiddlewareApiFactory().WithAllowedOrigins(Allowed);

        var response = await PreflightAsync(factory, Allowed);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Allowed, Header(response, "Access-Control-Allow-Origin"));
        Assert.Contains("POST", Header(response, "Access-Control-Allow-Methods") ?? "");
    }

    /// <summary>
    /// A preflight from an origin that is not on the list is answered, but with nothing granted — the
    /// browser then refuses to send the real request. Note the status is still 204, not 403: a
    /// rejected preflight is not an error response, so anything watching for 4xx will not see it.
    /// </summary>
    [Fact]
    public async Task APreflightFromADisallowedOriginGrantsNothing()
    {
        using var factory = new MiddlewareApiFactory().WithAllowedOrigins(Allowed);

        var response = await PreflightAsync(factory, Disallowed);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(Header(response, "Access-Control-Allow-Origin"));
        Assert.Null(Header(response, "Access-Control-Allow-Methods"));
    }

    /// <summary>
    /// With more than one allowed origin the allow-header depends on the request, so the response must
    /// carry <c>Vary: Origin</c> or an intermediary cache could serve one origin's header to another.
    /// Measured: ASP.NET Core emits it only when the policy names two or more origins — a single-origin
    /// allowlist gets no <c>Vary</c> at all. That is safe (the header is then constant for every
    /// request that gets one) but it is a behaviour worth pinning, because it means this assertion
    /// would silently stop testing anything if the fixture were reduced to one origin.
    /// </summary>
    [Fact]
    public async Task ResponsesVaryByOriginWhenMoreThanOneIsAllowed()
    {
        using var factory = new MiddlewareApiFactory().WithAllowedOrigins(Allowed, AlsoAllowed);

        var first = await GetAsync(factory, Allowed);
        var second = await GetAsync(factory, AlsoAllowed);

        Assert.Equal(Allowed, Header(first, "Access-Control-Allow-Origin"));
        Assert.Equal(AlsoAllowed, Header(second, "Access-Control-Allow-Origin"));
        Assert.Contains("Origin", Header(first, "Vary") ?? "");
        Assert.Contains("Origin", Header(second, "Vary") ?? "");
    }

    /// <summary>
    /// <strong>What restricting CORS does not do.</strong> A request from a disallowed origin still
    /// reaches the endpoint, still executes it, and still returns 200 with the complete body — the
    /// only difference is the absent allow-header, which is what stops the calling *script* reading
    /// the response. A non-browser client ignores the mechanism entirely.
    ///
    /// <para>This is asserted rather than written in a comment because the plan's justification for
    /// S7 — "any origin can drive the API with a stolen token" — is not what the change delivers. It
    /// bounds which web pages can use the API from a visitor's browser. A stolen token still works
    /// from anywhere, and authentication remains the only thing guarding the data.</para>
    /// </summary>
    [Fact]
    public async Task ADisallowedOriginIsStillServedByTheEndpoint()
    {
        using var factory = new MiddlewareApiFactory().WithAllowedOrigins(Allowed);
        factory.Source.CategoriesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)["beauty", "laptops"]);
        await factory.ResetUsersAsync(Username, Password);

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/products/categories");
        request.Headers.TryAddWithoutValidation("Origin", Disallowed);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Header(response, "Access-Control-Allow-Origin"));
        // The data left the process regardless. The browser is the only enforcement point.
        Assert.Equal(2, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetArrayLength());
    }

    /// <summary>
    /// An error response must carry the allow-header too. CORS runs ahead of authentication, and the
    /// 401 body is written by the challenge handler without clearing the response, so the header
    /// survives — meaning a browser client can actually read the RFC-7807 problem instead of seeing an
    /// opaque network failure. Worth pinning: the same body is unreadable if CORS is ever moved after
    /// authentication, and nothing else in the suite would notice.
    /// </summary>
    [Fact]
    public async Task AnErrorResponseStillCarriesTheAllowHeader()
    {
        using var factory = new MiddlewareApiFactory().WithAllowedOrigins(Allowed);

        var response = await GetAsync(factory, Allowed);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Allowed, Header(response, "Access-Control-Allow-Origin"));
    }

    // --- helpers -----------------------------------------------------------

    /// <summary>An unauthenticated GET: the CORS headers are written before authentication runs.</summary>
    private static async Task<HttpResponseMessage> GetAsync(MiddlewareApiFactory factory, string origin)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/products");
        request.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PreflightAsync(MiddlewareApiFactory factory, string origin)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/auth/login");
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "content-type");
        return await client.SendAsync(request);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private static async Task<string> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("token").GetString()!;
    }
}
