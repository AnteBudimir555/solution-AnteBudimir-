using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Middleware.Api.Errors;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Middleware.Api.OpenApi;

/// <summary>
/// OpenAPI document and UI — the springdoc replacement.
///
/// <para>On the net8.0 target <c>Microsoft.AspNetCore.OpenApi</c> only contributes endpoint metadata
/// (its document generator arrived in .NET 9), so Swashbuckle generates the document and serves the
/// UI. The generated contract is what matters, and it is pinned to the Java one: the same
/// <c>bearerAuth</c> scheme, the same per-operation security (public endpoints carry no lock), and the
/// same centrally-declared RFC-7807 error responses.</para>
/// </summary>
internal static class ApiDocumentationExtensions
{
    /// <summary>Scheme identifier shared by the component definition and the per-operation requirement.</summary>
    public const string BearerScheme = "bearerAuth";

    public const string DocumentName = "v1";

    public static IServiceCollection AddApiDocumentation(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc(DocumentName, new OpenApiInfo
            {
                Title = configuration["OpenApi:Title"] ?? "Product Middleware API",
                Description = configuration["OpenApi:Description"]
                    ?? "Middleware that re-exposes DummyJSON products with a trimmed shape, filtering, "
                       + "name search, JWT auth and caching.",
                Version = configuration["OpenApi:Version"] ?? "v1",
                License = new OpenApiLicense { Name = "MIT" }
            });

            // HTTP bearer scheme that makes the UI send "Authorization: Bearer <token>".
            options.AddSecurityDefinition(BearerScheme, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Paste a JWT obtained from POST /api/auth/login"
            });

            options.OperationFilter<ApiContractOperationFilter>();
        });

        return services;
    }

    public static WebApplication UseApiDocumentation(this WebApplication app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint($"/swagger/{DocumentName}/swagger.json", "Product Middleware API");
        });
        return app;
    }
}

/// <summary>
/// Aligns each generated operation with the Java contract in three respects: the bearer requirement is
/// attached per operation (so public endpoints show no lock), the common RFC-7807 error responses are
/// declared centrally, and parameter schemas report their real types.
/// </summary>
internal sealed class ApiContractOperationFilter : IOperationFilter
{
    private const string ProblemJson = ApiProblem.ContentType;
    private const string ProductsPathPrefix = "/api/products";

    /// <summary>
    /// Endpoint parameters are bound as strings so type-conversion failures can be reported as
    /// ProblemDetails (see <c>QueryParsing</c>); the documented schema keeps the real types.
    /// </summary>
    private static readonly Dictionary<string, (string Type, string? Format)> ParameterSchemas = new(StringComparer.Ordinal)
    {
        ["page"] = ("integer", "int32"),
        ["size"] = ("integer", "int32"),
        ["id"] = ("integer", "int64"),
        ["minPrice"] = ("number", null),
        ["maxPrice"] = ("number", null)
    };

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        ApplySecurity(operation, context);
        ApplyParameterSchemas(operation);
        ApplyErrorResponses(operation, context);
    }

    /// <summary>
    /// Attaches the bearer requirement only where it is actually enforced, so the documentation cannot
    /// drift from <c>ApiSecurityExtensions</c> as endpoints are added.
    /// </summary>
    private static void ApplySecurity(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any() || !metadata.OfType<IAuthorizeData>().Any())
        {
            return;
        }

        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = ApiDocumentationExtensions.BearerScheme
                }
            }] = []
        });
    }

    private static void ApplyParameterSchemas(OpenApiOperation operation)
    {
        foreach (var parameter in operation.Parameters ?? [])
        {
            if (!ParameterSchemas.TryGetValue(parameter.Name, out var schema))
            {
                continue;
            }
            parameter.Schema.Type = schema.Type;
            parameter.Schema.Format = schema.Format;
            parameter.Schema.Default = parameter.Name switch
            {
                "page" => new OpenApiInteger(0),
                "size" => new OpenApiInteger(20),
                _ => parameter.Schema.Default
            };
        }
    }

    /// <summary>
    /// Documents the common RFC-7807 error responses on every operation, centrally, so the generated
    /// spec matches the problem handler without per-endpoint annotations: 400/401/500 everywhere, 502
    /// on the product endpoints (which call the upstream source), and 404 wherever a path variable can
    /// address a missing resource.
    /// </summary>
    private static void ApplyErrorResponses(OpenApiOperation operation, OperationFilterContext context)
    {
        var problemSchema = context.SchemaGenerator.GenerateSchema(typeof(ProblemBody), context.SchemaRepository);

        AddProblemResponse(operation, problemSchema, "400", "Invalid request parameters or body");
        AddProblemResponse(operation, problemSchema, "401", "Missing or invalid bearer token");
        AddProblemResponse(operation, problemSchema, "500", "Unexpected server error");

        var path = "/" + (context.ApiDescription.RelativePath ?? string.Empty);
        if (path.StartsWith(ProductsPathPrefix, StringComparison.Ordinal))
        {
            AddProblemResponse(operation, problemSchema, "502", "Upstream product source error");
        }
        if (path.Contains('{', StringComparison.Ordinal))
        {
            AddProblemResponse(operation, problemSchema, "404", "Resource not found");
        }
    }

    private static void AddProblemResponse(
        OpenApiOperation operation, OpenApiSchema schema, string code, string description)
    {
        if (operation.Responses.ContainsKey(code))
        {
            return;
        }

        operation.Responses[code] = new OpenApiResponse
        {
            Description = description,
            Content = { [ProblemJson] = new OpenApiMediaType { Schema = schema } }
        };
    }
}
