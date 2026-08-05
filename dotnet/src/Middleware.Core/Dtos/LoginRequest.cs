namespace Middleware.Core.Dtos;

/// <summary>Credentials submitted to <c>POST /api/auth/login</c>. Both fields must be non-blank.</summary>
public sealed record LoginRequest(string Username, string Password);
