using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Middleware.Core.Options;

namespace Middleware.Infrastructure.Security;

/// <summary>
/// Issues and validates HS256 JWTs for locally-authenticated users. Tokens carry the username as the
/// subject and are signed with the configured secret; validation also enforces the issuer. The
/// algorithm is pinned explicitly so it never changes implicitly with the key length.
///
/// <para>Two <c>Microsoft.IdentityModel</c> defaults are deliberately overridden to match the original
/// jjwt behaviour: the five-minute <see cref="TokenValidationParameters.ClockSkew"/> is zeroed, and
/// default lifetime claims are suppressed so the payload carries exactly <c>sub</c>/<c>iss</c>/
/// <c>iat</c>/<c>exp</c> — no <c>nbf</c>, which jjwt never emitted.</para>
/// </summary>
public sealed class JwtService
{
    // SetDefaultTimesOnTokenCreation = false: iat/exp are written explicitly below, and no nbf is
    // added. This also keeps an already-expired token (negative expiry) constructible, which the
    // expiry-rejection test relies on.
    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    private readonly SymmetricSecurityKey _key;
    private readonly string _issuer;
    private readonly TimeSpan _expiration;

    public JwtService(IOptions<JwtOptions> options) : this(options.Value)
    {
    }

    public JwtService(JwtOptions options)
    {
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Secret));
        _issuer = options.Issuer;
        _expiration = TimeSpan.FromMinutes(options.ExpirationMinutes);
    }

    /// <summary>Seconds until an issued token expires (for the login response body).</summary>
    public long ExpiresInSeconds => (long)_expiration.TotalSeconds;

    /// <summary>
    /// Validation rules applied to every inbound token. Shared with the bearer authentication
    /// middleware so the endpoint pipeline and this service can never diverge.
    /// </summary>
    public TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuer = true,
        ValidIssuer = _issuer,
        // jjwt does not verify an audience, and no token this service mints carries one.
        ValidateAudience = false,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = _key,
        ValidateLifetime = true,
        // No grace period: an expired token is rejected the second it expires (jjwt parity).
        ClockSkew = TimeSpan.Zero,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        NameClaimType = JwtRegisteredClaimNames.Sub
    };

    /// <summary>Signs a token for the given username.</summary>
    public string IssueToken(string username)
    {
        var now = DateTimeOffset.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _issuer,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = username,
                [JwtRegisteredClaimNames.Iat] = now.ToUnixTimeSeconds(),
                [JwtRegisteredClaimNames.Exp] = now.Add(_expiration).ToUnixTimeSeconds()
            },
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256)
        };
        return Handler.CreateToken(descriptor);
    }

    /// <summary>
    /// Returns the subject (username) if the token is a valid, unexpired token signed by us and
    /// carrying the expected issuer, or <c>null</c> otherwise. Never throws on malformed input.
    /// </summary>
    public async Task<string?> ExtractUsernameAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var result = await Handler.ValidateTokenAsync(token, ValidationParameters);
        return result.IsValid ? (result.SecurityToken as JsonWebToken)?.Subject : null;
    }
}
