using Middleware.Api.Errors;
using Middleware.Api.Validation;
using Middleware.Core.Dtos;
using Middleware.Infrastructure.Persistence;
using Middleware.Infrastructure.Security;

namespace Middleware.Api.Endpoints;

/// <summary>
/// Authentication endpoint. Exchanges valid credentials for a signed JWT; bad credentials surface as
/// a 401 via the global problem handler.
/// </summary>
internal static class AuthEndpoints
{
    /// <summary>
    /// A well-formed BCrypt hash of a value no submitted password can match. Verifying against it when
    /// the username is unknown keeps the failure path's cost comparable to the wrong-password path, so
    /// response timing does not reveal which accounts exist — the same defence
    /// <c>DaoAuthenticationProvider</c> applies.
    /// </summary>
    private const string DummyHash = "$2a$10$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy";

    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/auth")
            .WithTags("Authentication")
            // Public endpoint: no bearer token required, and no Swagger lock (see AddApiDocumentation).
            .AllowAnonymous();

        group.MapPost("/login", LoginAsync)
            .WithName("Login")
            .WithSummary("Log in")
            .WithDescription("Authenticate with username/password and receive a JWT");

        return group;
    }

    private static async Task<LoginResponse> LoginAsync(
        LoginRequest? request,
        IUserRepository users,
        IPasswordHasher passwordHasher,
        JwtService jwtService,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (request is null)
        {
            throw new BadHttpRequestException("Failed to read request", StatusCodes.Status400BadRequest);
        }
        RequestValidation.EnsureValidBody(RequestValidators.Login, request);

        var user = await users.FindByUsernameAsync(request.Username, ct);
        if (user is null)
        {
            passwordHasher.Verify(request.Password, DummyHash);
            throw new InvalidCredentialsException($"Unknown user: {request.Username}");
        }
        if (!passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            throw new InvalidCredentialsException("Bad credentials");
        }

        var token = jwtService.IssueToken(user.Username);
        // Log the identity only — never the password or the issued token.
        loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!)
            .LogInformation("Issued JWT for user '{Username}'", user.Username);

        return LoginResponse.Bearer(token, jwtService.ExpiresInSeconds);
    }
}
