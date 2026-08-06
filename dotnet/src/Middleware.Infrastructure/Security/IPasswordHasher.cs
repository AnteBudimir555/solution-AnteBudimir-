namespace Middleware.Infrastructure.Security;

/// <summary>
/// Password hashing/verification. The abstraction exists so the algorithm is a composition-root
/// decision (and is substitutable in tests); the shipped implementation is BCrypt, matching the
/// original service so any migrated hash verifies unchanged.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string rawPassword);

    bool Verify(string rawPassword, string passwordHash);
}
