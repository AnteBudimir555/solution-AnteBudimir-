using Middleware.Core.Domain;

namespace Middleware.Infrastructure.Persistence;

/// <summary>
/// Persisted application user. Passwords are stored only as BCrypt hashes, never in plain text.
/// Setters are private and mutation goes through the constructor, mirroring the JPA entity's
/// encapsulation; the parameterless constructor exists only for EF materialization.
/// </summary>
public class UserAccount
{
    public long Id { get; private set; }
    public string Username { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public Role Role { get; private set; }

    private UserAccount()
    {
        // for EF Core
    }

    public UserAccount(string username, string passwordHash, Role role)
    {
        Username = username;
        PasswordHash = passwordHash;
        Role = role;
    }
}
