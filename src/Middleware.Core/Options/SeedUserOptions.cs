using System.ComponentModel.DataAnnotations;

namespace Middleware.Core.Options;

/// <summary>
/// Seed-user configuration, bound from <c>Security:SeedUser</c>. When enabled, a single user with
/// these credentials is created on startup so the API is usable out of the box. Disabled by default
/// so production-like configurations never create a known-credential account.
///
/// <para>Validated on start (fail fast): with seeding on, a blank username or password aborts
/// start-up rather than silently creating an account whose password is the empty string. The rule is
/// deliberately <em>not</em> gated on the hosting environment — an environment check is defeated by
/// setting <c>ASPNETCORE_ENVIRONMENT</c>, and gating here would fire in the integration suite, which
/// hosts the app as <c>Staging</c> and seeds on purpose.</para>
///
/// <para>Blankness is the only judgement the application can honestly make. Whether a given password
/// is <em>known</em> — because it came from a committed example file — is not visible from inside the
/// process, so that is enforced where provenance is knowable: <c>docker-compose.yml</c> requires both
/// values with <c>${VAR:?…}</c> and <c>.env.example</c> ships neither.</para>
/// </summary>
public sealed class SeedUserOptions : IValidatableObject
{
    public const string SectionName = "Security:SeedUser";

    public bool Enabled { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(Username)} is required when seeding is enabled.",
                [nameof(Username)]);
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(Password)} is required when seeding is enabled; "
                + "seeding an account with a blank password would leave it open to anyone.",
                [nameof(Password)]);
        }
    }
}
