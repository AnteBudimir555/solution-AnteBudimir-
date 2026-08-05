namespace Middleware.Core.Options;

/// <summary>
/// Seed-user configuration, bound from <c>Security:SeedUser</c>. When enabled, a single user with
/// these credentials is created on startup so the API is usable out of the box. Disabled by default
/// so production-like configurations never create a known-credential account.
/// </summary>
public sealed class SeedUserOptions
{
    public const string SectionName = "Security:SeedUser";

    public bool Enabled { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
