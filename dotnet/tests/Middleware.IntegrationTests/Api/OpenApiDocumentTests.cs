using System.Net;
using System.Text.Json;

namespace Middleware.IntegrationTests.Api;

/// <summary>
/// Contract checks on the generated OpenAPI document (the springdoc equivalent): it is publicly
/// readable, documents exactly the six endpoints, applies the bearer scheme everywhere it is enforced
/// — and nowhere it is not — and declares the RFC-7807 error responses centrally.
/// </summary>
public sealed class OpenApiDocumentTests(MiddlewareApiFactory factory) : IClassFixture<MiddlewareApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task DocumentIsServedWithoutAuthentication()
    {
        var response = await _client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SwaggerUiIsServedWithoutAuthentication()
    {
        var response = await _client.GetAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DocumentDescribesTheSixEndpoints()
    {
        var paths = (await DocumentAsync()).GetProperty("paths");

        Assert.Equal(
            ["/api/auth/login", "/api/products", "/api/products/categories", "/api/products/filter",
             "/api/products/search", "/api/products/{id}"],
            paths.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ProductOperationsRequireTheBearerSchemeAndLoginDoesNot()
    {
        var paths = (await DocumentAsync()).GetProperty("paths");

        Assert.True(RequiresBearer(paths, "/api/products", "get"));
        Assert.True(RequiresBearer(paths, "/api/products/{id}", "get"));
        Assert.True(RequiresBearer(paths, "/api/products/filter", "get"));
        Assert.True(RequiresBearer(paths, "/api/products/search", "get"));
        Assert.True(RequiresBearer(paths, "/api/products/categories", "get"));
        // Public endpoint: no lock in the UI, matching the security pipeline.
        Assert.False(RequiresBearer(paths, "/api/auth/login", "post"));
    }

    [Fact]
    public async Task BearerSchemeIsDeclaredOnce()
    {
        var scheme = (await DocumentAsync())
            .GetProperty("components").GetProperty("securitySchemes").GetProperty("bearerAuth");

        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
        Assert.Equal("JWT", scheme.GetProperty("bearerFormat").GetString());
    }

    [Fact]
    public async Task EveryOperationDocumentsTheProblemDetailErrorResponses()
    {
        var paths = (await DocumentAsync()).GetProperty("paths");

        foreach (var path in paths.EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var responses = operation.Value.GetProperty("responses");
                foreach (var code in new[] { "400", "401", "500" })
                {
                    Assert.True(responses.TryGetProperty(code, out var problem), $"{path.Name} {operation.Name} {code}");
                    Assert.True(problem.GetProperty("content").TryGetProperty("application/problem+json", out _));
                }
                if (path.Name.StartsWith("/api/products", StringComparison.Ordinal))
                {
                    Assert.True(responses.TryGetProperty("502", out _), $"{path.Name} 502");
                }
                if (path.Name.Contains('{', StringComparison.Ordinal))
                {
                    Assert.True(responses.TryGetProperty("404", out _), $"{path.Name} 404");
                }
            }
        }
    }

    [Fact]
    public async Task NumericParametersKeepTheirDeclaredTypes()
    {
        var parameters = (await DocumentAsync())
            .GetProperty("paths").GetProperty("/api/products/filter").GetProperty("get").GetProperty("parameters");

        Assert.Equal("integer", SchemaTypeOf(parameters, "page"));
        Assert.Equal("integer", SchemaTypeOf(parameters, "size"));
        Assert.Equal("number", SchemaTypeOf(parameters, "minPrice"));
        Assert.Equal("number", SchemaTypeOf(parameters, "maxPrice"));
        Assert.Equal("string", SchemaTypeOf(parameters, "category"));
    }

    private static bool RequiresBearer(JsonElement paths, string path, string method) =>
        paths.GetProperty(path).GetProperty(method).TryGetProperty("security", out var security)
        && security.EnumerateArray().Any(requirement => requirement.TryGetProperty("bearerAuth", out _));

    private static string? SchemaTypeOf(JsonElement parameters, string name) =>
        parameters.EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == name)
            .GetProperty("schema").GetProperty("type").GetString();

    private async Task<JsonElement> DocumentAsync() =>
        JsonDocument.Parse(await _client.GetStringAsync("/swagger/v1/swagger.json")).RootElement;
}
