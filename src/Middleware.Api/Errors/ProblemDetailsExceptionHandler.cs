using Microsoft.AspNetCore.Diagnostics;
using Middleware.Core.Exceptions;

namespace Middleware.Api.Errors;

/// <summary>
/// Centralized error handling — the <see cref="IExceptionHandler"/> analog of the Java
/// <c>@RestControllerAdvice</c>. Every failure is rendered as an RFC-7807 <see cref="ProblemBody"/>
/// with a sensible HTTP status, so clients get a consistent error body across the API.
///
/// <para>The handler always returns <c>true</c>: an unrecognised exception falls through to a generic
/// 500 rather than to the framework's default renderer, which would emit a different shape.</para>
/// </summary>
public sealed class ProblemDetailsExceptionHandler(ILogger<ProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        await ApiProblem.WriteAsync(context, Translate(context, exception));
        return true;
    }

    private ProblemBody Translate(HttpContext context, Exception exception)
    {
        switch (exception)
        {
            case ProductNotFoundException notFound:
                return ApiProblem.Create(context, StatusCodes.Status404NotFound,
                    "Product not found", notFound.Message, "product-not-found");

            case UpstreamException upstream:
                logger.LogError("Upstream failure: {Reason}", upstream.Message);
                return ApiProblem.Create(context, StatusCodes.Status502BadGateway,
                    "Upstream source error", "The product source is currently unavailable.", "upstream-error");

            // Semantically invalid request whose own message is already client-safe
            // (e.g. minPrice > maxPrice).
            case InvalidRequestException invalid:
                return ApiProblem.Create(context, StatusCodes.Status400BadRequest,
                    "Invalid request", invalid.Message, "bad-request");

            case ParameterTypeMismatchException mismatch:
                return ApiProblem.Create(context, StatusCodes.Status400BadRequest,
                    "Invalid request", mismatch.Message, "bad-request");

            case MissingParameterException missing:
                return ApiProblem.Create(context, StatusCodes.Status400BadRequest,
                    "Bad Request", missing.Message, "bad-request");

            case RequestValidationException validation:
                return ApiProblem.Create(context, StatusCodes.Status400BadRequest,
                    "Validation failed",
                    string.IsNullOrWhiteSpace(validation.Message) ? "Invalid request" : validation.Message,
                    "validation-failed");

            case InvalidCredentialsException credentials:
                // Log the real reason (never the submitted credentials), but return a generic detail:
                // the specific cause (unknown user vs. bad password) must not leak to the client, as
                // it enables account enumeration.
                logger.LogWarning("Authentication failed: {Reason}", credentials.Message);
                return ApiProblem.Create(context, StatusCodes.Status401Unauthorized,
                    "Authentication failed", "Invalid username or password.", "unauthorized");

            // Malformed or missing request body: the framework rejected it before model binding.
            case BadHttpRequestException badRequest:
                return ApiProblem.Create(context, badRequest.StatusCode,
                    "Bad Request", "Failed to read request", "bad-request");

            default:
                logger.LogError(exception, "Unexpected error");
                return ApiProblem.Create(context, StatusCodes.Status500InternalServerError,
                    "Internal server error", "An unexpected error occurred.", "internal-error");
        }
    }
}
