using System.ComponentModel.DataAnnotations;

namespace Middleware.Core.Options;

/// <summary>
/// JWT signing configuration, bound from <c>Jwt</c>. Validated on startup (fail fast) rather than
/// surfacing later as runtime errors. The secret must be at least 32 characters for HS256; supply it
/// via configuration/environment in real environments — there is no default.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    [MinLength(32, ErrorMessage = "JWT secret must be at least 32 characters for HS256")]
    public string Secret { get; set; } = string.Empty;

    [Range(1, long.MaxValue)]
    public long ExpirationMinutes { get; set; } = 60;

    [Required]
    public string Issuer { get; set; } = string.Empty;
}
