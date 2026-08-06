using System.ComponentModel.DataAnnotations;

namespace Middleware.Core.Options;

/// <summary>
/// Browser origins permitted to read responses from this API, bound from <c>Cors</c>. Named
/// <c>CorsPolicyOptions</c> rather than <c>CorsOptions</c> so it never collides with ASP.NET Core's
/// own type of that name at a call site that imports both.
///
/// <para><strong>The default is an empty list, which allows no browser origin.</strong> That is the
/// safe direction for an API whose callers are overwhelmingly server-side: a deployment that needs
/// browser access declares the origins it needs, and one that does not never grants any.</para>
///
/// <para><strong>What this does and does not do.</strong> CORS is enforced by the browser, not by the
/// server. Measured against a running host: a request from a disallowed origin still reaches the
/// endpoint, still runs it, and still returns 200 with the full body — only the missing
/// <c>Access-Control-Allow-Origin</c> header stops the calling script from reading it, and a
/// non-browser client ignores the whole mechanism. So this bounds which *web pages* can use the API
/// from a visitor's browser; it is not an access control, and it stops nothing holding a valid token.
/// Authentication remains the only thing guarding the data.</para>
/// </summary>
public sealed class CorsPolicyOptions : IValidatableObject
{
    public const string SectionName = "Cors";

    /// <summary>
    /// Exact origins — scheme, host and (if non-default) port, with no trailing slash and no path.
    /// Validated on start-up because every way of getting one wrong fails silently at runtime.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// Rejects the origin spellings that produce no error and simply never match. Measured on the
    /// running middleware: a configured <c>"https://app.example.com/"</c> does not match an
    /// <c>Origin: https://app.example.com</c> request — no header is emitted, nothing is logged, and
    /// the symptom is a browser client that is blocked for no visible reason. Host casing, by
    /// contrast, *is* normalized and matches either way, so it is not rejected here.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var origin in AllowedOrigins)
        {
            var member = new[] { nameof(AllowedOrigins) };

            if (string.IsNullOrWhiteSpace(origin))
            {
                yield return new ValidationResult("Cors:AllowedOrigins contains a blank entry.", member);
                continue;
            }

            // "*" would restore AllowAnyOrigin through the allowlist, which is the thing this option
            // exists to remove. WithOrigins("*") is in fact honoured by the framework as a wildcard.
            if (origin == "*")
            {
                yield return new ValidationResult(
                    "Cors:AllowedOrigins must not contain \"*\". Leave the list empty to allow no browser " +
                    "origin, or name each origin explicitly.", member);
                continue;
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                yield return new ValidationResult(
                    $"Cors:AllowedOrigins entry '{origin}' is not an absolute URI. Expected a scheme and host, " +
                    "for example 'https://app.example.com'.", member);
                continue;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                yield return new ValidationResult(
                    $"Cors:AllowedOrigins entry '{origin}' must use http or https, not '{uri.Scheme}'.", member);
            }

            if (origin.EndsWith('/'))
            {
                yield return new ValidationResult(
                    $"Cors:AllowedOrigins entry '{origin}' must not end with '/'. An Origin header never " +
                    "carries a trailing slash, so this entry would silently match nothing.", member);
            }
            else if (uri.AbsolutePath.Length > 1 || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                yield return new ValidationResult(
                    $"Cors:AllowedOrigins entry '{origin}' must be an origin only — scheme, host and port, " +
                    "with no path, query or fragment.", member);
            }
        }
    }
}
