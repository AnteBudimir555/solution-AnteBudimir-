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

    /// <summary>
    /// Every validation bound the service enforces is also published, as springdoc publishes the Bean
    /// Validation annotations. Without these the document would promise limits the service does not
    /// have — the OpenAPI diff against the Java document is what surfaced the omission.
    /// </summary>
    [Fact]
    public async Task ParameterBoundsMatchTheEnforcedConstraints()
    {
        var paths = (await DocumentAsync()).GetProperty("paths");
        var filter = paths.GetProperty("/api/products/filter").GetProperty("get").GetProperty("parameters");

        Assert.Equal((0, 10000), RangeOf(filter, "page"));
        Assert.Equal((1, 100), RangeOf(filter, "size"));
        Assert.Equal(0, ParameterOf(filter, "minPrice").GetProperty("schema").GetProperty("minimum").GetInt32());
        Assert.Equal(100, ParameterOf(filter, "category").GetProperty("schema").GetProperty("maxLength").GetInt32());

        // q is the one query parameter with no default: omitting it is an error, not a default.
        var search = paths.GetProperty("/api/products/search").GetProperty("get").GetProperty("parameters");
        Assert.True(ParameterOf(search, "q").GetProperty("required").GetBoolean());
        // An omitted "required" is false per the spec, which is how the generator writes optional ones.
        Assert.False(ParameterOf(search, "page").TryGetProperty("required", out var pageRequired)
                     && pageRequired.GetBoolean());
    }

    /// <summary>
    /// Component names drive the class names a generated client produces, so they are part of the
    /// contract: the problem body is published as <c>ProblemDetail</c>, and the paged envelope keeps
    /// springdoc's prefix-first generic name.
    /// </summary>
    [Fact]
    public async Task SchemaNamesMatchTheJavaDocument()
    {
        var schemas = (await DocumentAsync()).GetProperty("components").GetProperty("schemas");

        Assert.True(schemas.TryGetProperty("ProblemDetail", out _));
        Assert.True(schemas.TryGetProperty("PagedResponseProductSummaryDto", out _));
        Assert.False(schemas.TryGetProperty("ProblemBody", out _));
        Assert.False(schemas.TryGetProperty("ProductSummaryDtoPagedResponse", out _));
    }

    /// <summary>
    /// Prices are decimal end-to-end. A <c>format: double</c> in the document would tell a generator to
    /// bind them to a binary float, reintroducing exactly the precision loss the domain model avoids.
    /// </summary>
    [Fact]
    public async Task PriceIsANumberWithoutABinaryFloatFormat()
    {
        var price = (await DocumentAsync()).GetProperty("components").GetProperty("schemas")
            .GetProperty("ProductSummaryDto").GetProperty("properties").GetProperty("price");

        Assert.Equal("number", price.GetProperty("type").GetString());
        Assert.False(price.TryGetProperty("format", out _));
    }

    private static (int Minimum, int Maximum) RangeOf(JsonElement parameters, string name)
    {
        var schema = ParameterOf(parameters, name).GetProperty("schema");
        return (schema.GetProperty("minimum").GetInt32(), schema.GetProperty("maximum").GetInt32());
    }

    private static JsonElement ParameterOf(JsonElement parameters, string name) =>
        parameters.EnumerateArray().Single(p => p.GetProperty("name").GetString() == name);

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
