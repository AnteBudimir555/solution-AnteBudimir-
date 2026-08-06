using System.Diagnostics;
using System.Text.RegularExpressions;
using Serilog.Context;

namespace Middleware.Api.Middleware;

/// <summary>
/// Tags every request with a correlation id and emits one completion log line.
///
/// <para>The id is pushed onto Serilog's <see cref="LogContext"/> (the SLF4J MDC analog), so every log
/// line written while handling the request can be tied back to a single client call. It is also echoed
/// on the response as <see cref="CorrelationIdHeader"/> so clients and downstream systems can quote it
/// when reporting issues.</para>
///
/// <para>The request method and path (and, on completion, the status and duration) are pushed as
/// discrete properties too, so the compact-JSON sink emits them as queryable fields; the readable
/// console template renders only the correlation id.</para>
///
/// <para>Registered first in the pipeline so the id exists before authentication runs —
/// authentication/authorization failures rendered from inside that stage still carry it. The pushed
/// properties are scoped to the request via <c>using</c>, so nothing leaks onto a pooled thread.</para>
///
/// <para>No request headers or bodies are logged, so credentials (Authorization header, login payload)
/// never reach the logs. An inbound id is only reused when it matches a strict safe pattern, which
/// also prevents log-injection via crafted header values.</para>
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    /// <summary>Request/response header carrying the correlation id.</summary>
    public const string CorrelationIdHeader = "X-Correlation-Id";

    /// <summary>Log-context property name; must match the <c>{correlationId}</c> output template token.</summary>
    public const string CorrelationIdProperty = "correlationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context.Request);
        var path = RequestPath(context.Request);
        var method = context.Request.Method;

        context.Response.Headers[CorrelationIdHeader] = correlationId;

        using (LogContext.PushProperty(CorrelationIdProperty, correlationId))
        using (LogContext.PushProperty("method", method))
        using (LogContext.PushProperty("path", path))
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await next(context);
            }
            finally
            {
                // Single completion line (also emitted on failure, via finally): it carries method,
                // path, status and duration, so a separate entry line would only add noise.
                var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                using (LogContext.PushProperty("status", context.Response.StatusCode))
                using (LogContext.PushProperty("durationMs", elapsedMs))
                {
                    logger.LogInformation("{Method} {Path} -> {StatusCode} ({ElapsedMs} ms)",
                        method, path, context.Response.StatusCode, elapsedMs);
                }
            }
        }
    }

    private static string ResolveCorrelationId(HttpRequest request)
    {
        var inbound = request.Headers[CorrelationIdHeader].ToString();
        return SafeCorrelationId().IsMatch(inbound) ? inbound : Guid.NewGuid().ToString();
    }

    private static string RequestPath(HttpRequest request) =>
        request.QueryString.HasValue ? request.Path + request.QueryString.Value : request.Path.ToString();

    /// <summary>Accept an inbound id only if it is short and free of control characters (log-injection safe).</summary>
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex SafeCorrelationId();
}
