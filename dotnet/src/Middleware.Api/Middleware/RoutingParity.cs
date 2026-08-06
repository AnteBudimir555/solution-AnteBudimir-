namespace Middleware.Api.Middleware;

/// <summary>
/// Two narrow pipeline steps that keep route resolution answering what the Java service answers. Both
/// exist because Spring resolves security and handlers by <em>path</em>, while ASP.NET Core resolves
/// them by <em>endpoint</em> — a difference that is invisible on a matched request and visible on a
/// near-miss. Each was found by the API-shadowing harness, not predicted.
/// </summary>
internal static class RoutingParity
{
    /// <summary>Path prefixes the Java security chain permits without a token.</summary>
    private static readonly string[] PublicPrefixes = ["/api/auth"];

    /// <summary>
    /// Rejects a trailing slash on the API surface. ASP.NET Core's matcher treats
    /// <c>/api/products/</c> as <c>/api/products</c> and serves the list; Spring matches nothing and
    /// answers 404. Setting the status here lets the shared status-code handler render the same problem
    /// body an unknown path produces.
    ///
    /// <para>Scoped to <c>/api/</c> deliberately: the Swagger UI relies on a trailing-slash redirect,
    /// and the Java service leaves its documentation routes alone too.</para>
    /// </summary>
    public static IApplicationBuilder UseTrailingSlashRejection(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            var path = context.Request.Path.Value;
            if (path is { Length: > 1 } && path[^1] == '/'
                && path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            return next();
        });

    /// <summary>
    /// Lets a method mismatch on a public path answer 405 instead of 401.
    ///
    /// <para>When no route matches the request's method, routing selects a framework short-circuit
    /// endpoint that carries no metadata. The fallback authorization policy — the analog of Spring's
    /// <c>anyRequest().authenticated()</c> — therefore applies to it and challenges, so
    /// <c>GET /api/auth/login</c> answers 401 where Spring, having permitted the path outright, answers
    /// 405. Running that endpoint directly on the permitted prefixes restores Spring's path-based
    /// semantics exactly where Spring is path-based, and is the more correct answer regardless: a public
    /// path should not demand a token it never uses.</para>
    ///
    /// <para>Protected paths are untouched, so a wrong method with no token still answers 401 there —
    /// which is also what the Java security chain does, since it runs ahead of handler resolution.</para>
    /// </summary>
    public static IApplicationBuilder UsePublicPathMethodMismatch(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            // A real route resolves to a RouteEndpoint; the framework's 405/415 short-circuits do not.
            if (context.GetEndpoint() is { RequestDelegate: { } run } and not RouteEndpoint
                && IsPublic(context.Request.Path))
            {
                return run(context);
            }

            return next();
        });

    private static bool IsPublic(PathString path) =>
        PublicPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
}
