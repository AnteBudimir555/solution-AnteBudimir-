using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using Middleware.Api.Errors;
using Middleware.Core.Domain;
using Middleware.Infrastructure.Persistence;
using Middleware.Infrastructure.Security;

namespace Middleware.Api.Security;

/// <summary>
/// Stateless bearer-token security. Public: the auth endpoints and the OpenAPI docs. Everything else —
/// including unmatched routes — requires a valid token, via the fallback authorization policy.
///
/// <para>Authentication (401) and authorization (403) failures happen inside the authentication
/// middleware, before any endpoint runs. Rather than duplicate error rendering, both events write the
/// same <see cref="ProblemBody"/> the exception handler produces, so the RFC-7807 contract is uniform
/// across the whole API.</para>
/// </summary>
internal static class ApiSecurityExtensions
{
    public static IServiceCollection AddApiSecurity(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // "sub" must stay "sub": the inbound claim-type map would otherwise rewrite it to the
                // long WS-Federation ClaimTypes.NameIdentifier URI and silently change lookups.
                options.MapInboundClaims = false;
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = EnsureUserStillExistsAsync,
                    OnChallenge = WriteUnauthorizedProblemAsync,
                    OnForbidden = WriteForbiddenProblemAsync
                };
            });

        // The validation rules come from JwtService rather than being restated here, so the middleware
        // and the service can never validate by different rules (zero clock skew, enforced issuer, no
        // audience, HS256 only).
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwtService>((options, jwtService) =>
                options.TokenValidationParameters = jwtService.ValidationParameters);

        services.AddAuthorization(options =>
            // The analog of Spring's anyRequest().authenticated(): applied to every endpoint that does
            // not opt out with AllowAnonymous, including the catch-all fallback route.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        return services;
    }

    /// <summary>
    /// Re-checks the token's subject against the user store, mirroring the Java filter's
    /// <c>UserDetailsService</c> lookup: a token that is cryptographically valid but names a user who
    /// no longer exists must not authenticate. Also attaches the stored role as the authority.
    /// </summary>
    private static async Task EnsureUserStillExistsAsync(TokenValidatedContext context)
    {
        var username = context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrEmpty(username))
        {
            context.Fail("Token carries no subject.");
            return;
        }

        var users = context.HttpContext.RequestServices.GetRequiredService<IUserRepository>();
        var user = await users.FindByUsernameAsync(username, context.HttpContext.RequestAborted);
        if (user is null)
        {
            context.Fail($"Unknown user: {username}");
            return;
        }

        context.Principal!.AddIdentity(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.Authority())
        ]));
    }

    private static Task WriteUnauthorizedProblemAsync(JwtBearerChallengeContext context)
    {
        // Suppress the framework's empty-bodied 401 (and its WWW-Authenticate header) in favour of the
        // API's problem body.
        context.HandleResponse();
        return ApiProblem.WriteAsync(context.HttpContext, ApiProblem.Create(
            context.HttpContext, StatusCodes.Status401Unauthorized,
            "Authentication failed", "Invalid username or password.", "unauthorized"));
    }

    private static Task WriteForbiddenProblemAsync(ForbiddenContext context) =>
        ApiProblem.WriteAsync(context.HttpContext, ApiProblem.Create(
            context.HttpContext, StatusCodes.Status403Forbidden,
            "Access denied", "Access Denied", "forbidden"));
}
