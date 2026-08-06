using Microsoft.AspNetCore.Authorization;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Middleware.Api.Errors;
using Middleware.Api.Validation;
using Middleware.Core.Dtos;
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

            // Prices are decimal (Java BigDecimal). Swashbuckle's default mapping adds format "double",
            // which would tell a code generator to bind them to a binary float — the very precision loss
            // the domain model avoids. Number without a format is what springdoc publishes.
            options.MapType<decimal>(() => new OpenApiSchema { Type = JsonSchemaType.Number });
            options.MapType<decimal?>(() =>
                new OpenApiSchema { Type = JsonSchemaType.Number | JsonSchemaType.Null });

            options.CustomSchemaIds(SchemaId);
            options.OperationFilter<ApiContractOperationFilter>();
            options.SchemaFilter<LoginRequestConstraintsFilter>();
            options.SchemaFilter<PublishedDescriptionsFilter>();
        });

        return services;
    }

    /// <summary>
    /// Component names as springdoc emits them, so <c>$ref</c>s — and therefore the classes a generated
    /// client produces — are unchanged: the problem body is <c>ProblemDetail</c> (the Spring type it
    /// reproduces), and a generic is named prefix-first (<c>PagedResponseProductSummaryDto</c>) rather
    /// than Swashbuckle's argument-first default.
    /// </summary>
    private static string SchemaId(Type type)
    {
        if (type == typeof(ProblemBody))
        {
            return "ProblemDetail";
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return name + string.Concat(type.GetGenericArguments().Select(SchemaId));
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
    /// ProblemDetails (see <c>QueryParsing</c>); the documented schema restores the real types.
    ///
    /// <para>The bounds are the same ones <see cref="Middleware.Api.Validation.RequestValidators"/>
    /// enforces. Java gets them into the document for free, because springdoc reads the Bean Validation
    /// annotations off the controller signature; here they have to be stated, or the document would
    /// promise no limits on parameters the service does in fact reject.</para>
    /// </summary>
    private static readonly Dictionary<string, ParameterContract> ParameterSchemas = new(StringComparer.Ordinal)
    {
        ["page"] = new(JsonSchemaType.Integer, "int32", Minimum: 0, Maximum: RequestValidators.MaxPage, Default: 0),
        ["size"] = new(JsonSchemaType.Integer, "int32", Minimum: 1, Maximum: 100, Default: 20),
        ["id"] = new(JsonSchemaType.Integer, "int64", Required: true),
        ["minPrice"] = new(JsonSchemaType.Number, null, Minimum: 0),
        ["maxPrice"] = new(JsonSchemaType.Number, null, Minimum: 0),
        ["category"] = new(JsonSchemaType.String, null, MinLength: 0, MaxLength: RequestValidators.MaxTextParam),
        // Unlike every other query parameter, q has no default: omitting it is an error, not a default.
        ["q"] = new(JsonSchemaType.String, null, MinLength: 0, MaxLength: RequestValidators.MaxTextParam, Required: true)
    };

    /// <summary>The documented contract of one parameter: its type, its bounds and whether it is required.</summary>
    private sealed record ParameterContract(
        JsonSchemaType Type,
        string? Format,
        decimal? Minimum = null,
        decimal? Maximum = null,
        int? MinLength = null,
        int? MaxLength = null,
        bool Required = false,
        JsonNode? Default = null);

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        ApplySecurity(operation, context);
        ApplyParameterSchemas(operation);
        ApplyErrorResponses(operation, context);

        // Minimal APIs bind the body as a nullable parameter so a missing one can be rejected with the
        // API's own problem body rather than the framework's; the contract still requires it, as
        // Spring's @RequestBody does.
        if (operation.RequestBody is OpenApiRequestBody requestBody)
        {
            requestBody.Required = true;
        }
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

        operation.Security ??= [];
        // The host document is not optional here: a reference without one resolves to no name and the
        // requirement serializes as an empty `{}`, which reads as "no authentication required".
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(ApiDocumentationExtensions.BearerScheme, context.Document)] = []
        });
    }

    private static void ApplyParameterSchemas(OpenApiOperation operation)
    {
        // Only a concrete parameter/schema is mutable; the interfaces a document can also hold are
        // references, which carry nothing of their own to set.
        foreach (var parameter in (operation.Parameters ?? []).OfType<OpenApiParameter>())
        {
            if (parameter.Name is not { } name || !ParameterSchemas.TryGetValue(name, out var contract))
            {
                continue;
            }

            parameter.Required = contract.Required;
            if (parameter.Schema is not OpenApiSchema schema)
            {
                continue;
            }

            schema.Type = contract.Type;
            schema.Format = contract.Format;
            // Bounds are strings in this model, so an exact decimal survives into the document
            // instead of being routed through a binary float.
            schema.Minimum = contract.Minimum?.ToString(CultureInfo.InvariantCulture);
            schema.Maximum = contract.Maximum?.ToString(CultureInfo.InvariantCulture);
            schema.MinLength = contract.MinLength;
            schema.MaxLength = contract.MaxLength;
            schema.Default = contract.Default ?? schema.Default;
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
        OpenApiOperation operation, IOpenApiSchema schema, string code, string description)
    {
        operation.Responses ??= [];
        if (operation.Responses.ContainsKey(code))
        {
            return;
        }

        operation.Responses[code] = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
            {
                [ProblemJson] = new OpenApiMediaType { Schema = schema }
            }
        };
    }
}

/// <summary>
/// Publishes the login body's <c>@NotBlank</c> constraints, which springdoc reads off the Java record
/// and Swashbuckle cannot infer: both fields are required and must be non-empty.
/// </summary>
internal sealed class LoginRequestConstraintsFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type != typeof(LoginRequest) || schema is not OpenApiSchema concrete)
        {
            return;
        }

        concrete.Required ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in concrete.Properties ?? new Dictionary<string, IOpenApiSchema>())
        {
            concrete.Required.Add(property.Key);
            if (property.Value is OpenApiSchema field)
            {
                field.MinLength = 1;
            }
        }
    }
}

/// <summary>
/// Restates the descriptions and string formats springdoc publishes from the Java records'
/// <c>@Schema</c> annotations, so the two documents describe the API in the same words.
///
/// <para>They live here rather than in XML doc comments on the DTOs because they are contract, not
/// developer commentary: the C# <c>&lt;summary&gt;</c> blocks explain a type to whoever maintains it,
/// while these strings are published to API consumers and have to match the Java service's exactly.</para>
/// </summary>
internal sealed class PublishedDescriptionsFilter : ISchemaFilter
{
    private static readonly Dictionary<Type, string> TypeDescriptions = new()
    {
        [typeof(ProductSummaryDto)] = "Trimmed product summary for list/filter/search results",
        [typeof(ProductDetailDto)] = "Full product detail",
        [typeof(PagedResponse<ProductSummaryDto>)] = "Paginated response envelope"
    };

    private static readonly Dictionary<(Type Owner, string Property), string> PropertyDescriptions = new()
    {
        [(typeof(ProductSummaryDto), "image")] = "Product thumbnail image URL",
        [(typeof(ProductSummaryDto), "name")] = "Product name",
        [(typeof(ProductSummaryDto), "price")] = "Product price",
        [(typeof(ProductSummaryDto), "shortDescription")] = "Description truncated to at most 100 characters",
        [(typeof(LoginRequest), "username")] = "Account username",
        [(typeof(LoginRequest), "password")] = "Account password",
        [(typeof(LoginResponse), "token")] = "Signed JWT to send as 'Authorization: Bearer <token>'",
        [(typeof(LoginResponse), "tokenType")] = "Token scheme to use in the Authorization header",
        [(typeof(LoginResponse), "expiresInSeconds")] = "Seconds until the token expires"
    };

    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema concrete)
        {
            return;
        }

        if (TypeDescriptions.TryGetValue(context.Type, out var description))
        {
            concrete.Description = description;
        }

        foreach (var property in concrete.Properties ?? new Dictionary<string, IOpenApiSchema>())
        {
            if (property.Value is not OpenApiSchema field)
            {
                continue;
            }

            if (PropertyDescriptions.TryGetValue((context.Type, property.Key), out var propertyDescription))
            {
                field.Description = propertyDescription;
            }

            // Spring's ProblemDetail types both members as a URI; the values are one absolute and one
            // relative reference, and both are URIs.
            if (context.Type == typeof(ProblemBody) && property.Key is "type" or "instance")
            {
                field.Format = "uri";
            }
        }
    }
}
