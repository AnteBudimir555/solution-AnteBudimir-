using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Middleware.Api.Errors;

/// <summary>
/// RFC-7807 problem body, serialized for every error the API produces.
///
/// <para>A dedicated record is used rather than ASP.NET Core's <c>ProblemDetails</c> so the wire shape
/// matches Spring's <c>ProblemDetail</c> exactly: the same member order, the custom <c>timestamp</c>
/// property, and no framework extensions (<c>traceId</c>, <c>errors</c>) leaking into the body.</para>
/// </summary>
public sealed record ProblemBody(
    string Type,
    string Title,
    int Status,
    string Detail,
    string? Instance,
    string Timestamp);

/// <summary>
/// Builds and writes <see cref="ProblemBody"/> responses. Shared by the exception handler, the bearer
/// authentication events and the status-code page handler, so every error — whether it originates in a
/// handler, in the security pipeline or in the framework — is rendered identically.
/// </summary>
public static class ApiProblem
{
    /// <summary>Namespace for this API's error <c>type</c> URIs.</summary>
    public const string TypePrefix = "https://abysalto.middleware/errors/";

    /// <summary>RFC-7807 media type produced for every error.</summary>
    public const string ContentType = "application/problem+json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static ProblemBody Create(HttpContext context, int status, string title, string detail, string type) =>
        new(TypePrefix + type,
            title,
            status,
            detail,
            context.Request.Path.HasValue ? context.Request.Path.Value : null,
            // Instant.toString() parity: ISO-8601, UTC, 'Z' suffix.
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

    /// <summary>
    /// Builds the problem for a bare framework status (unknown route, unsupported method/media type,
    /// …), applying this API's <c>type</c>/<c>timestamp</c> convention to a response that would
    /// otherwise have no body at all.
    /// </summary>
    public static ProblemBody ForStatus(HttpContext context, int status)
    {
        var reason = ReasonPhrases.GetReasonPhrase(status);
        var title = string.IsNullOrEmpty(reason) ? "Error" : reason;
        return Create(context, status, title, title, Slug(status));
    }

    public static async Task WriteAsync(HttpContext context, ProblemBody problem)
    {
        context.Response.StatusCode = problem.Status;
        context.Response.ContentType = ContentType;
        await context.Response.WriteAsJsonAsync(problem, SerializerOptions, ContentType, context.RequestAborted);
    }

    /// <summary>
    /// Kebab-cased status name used as the <c>type</c> slug for framework-produced errors, matching
    /// Java's <c>HttpStatus.name().toLowerCase().replace('_', '-')</c> (e.g. 405 →
    /// <c>method-not-allowed</c>).
    /// </summary>
    private static string Slug(int status)
    {
        var name = Enum.IsDefined(typeof(System.Net.HttpStatusCode), status)
            ? ((System.Net.HttpStatusCode)status).ToString()
            : null;
        if (name is null)
        {
            return "error";
        }

        var slug = new StringBuilder(name.Length + 4);
        foreach (var c in name)
        {
            if (char.IsUpper(c) && slug.Length > 0)
            {
                slug.Append('-');
            }
            slug.Append(char.ToLowerInvariant(c));
        }
        return slug.ToString();
    }
}
