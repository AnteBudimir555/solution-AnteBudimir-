namespace Middleware.Core.Dtos;

/// <summary>Successful login response carrying the signed JWT and how to use it.</summary>
public sealed record LoginResponse(string Token, string TokenType, long ExpiresInSeconds)
{
    public static LoginResponse Bearer(string token, long expiresInSeconds) =>
        new(token, "Bearer", expiresInSeconds);
}
