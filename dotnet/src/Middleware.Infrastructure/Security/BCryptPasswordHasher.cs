namespace Middleware.Infrastructure.Security;

/// <summary>
/// BCrypt password hashing, the direct analog of Spring Security's <c>BCryptPasswordEncoder</c>.
/// The work factor is pinned to 10 — the encoder's default strength — so hashes produced here and
/// hashes migrated from the Java service are interchangeable in both directions.
/// </summary>
public sealed class BCryptPasswordHasher : IPasswordHasher
{
    /// <summary>Cost matching <c>BCryptPasswordEncoder</c>'s default strength.</summary>
    private const int WorkFactor = 10;

    public string Hash(string rawPassword) => BCrypt.Net.BCrypt.HashPassword(rawPassword, WorkFactor);

    public bool Verify(string rawPassword, string passwordHash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(rawPassword, passwordHash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            // A stored value that is not a BCrypt hash can never match; treat it as a failed
            // verification rather than a server error.
            return false;
        }
    }
}
