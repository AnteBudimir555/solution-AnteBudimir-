namespace Middleware.Infrastructure.Persistence;

/// <summary>
/// Persistence gateway for <see cref="UserAccount"/>. Lookup by username backs authentication;
/// existence check and add back the startup seeder.
/// </summary>
public interface IUserRepository
{
    Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default);

    Task<bool> ExistsByUsernameAsync(string username, CancellationToken ct = default);

    Task AddAsync(UserAccount user, CancellationToken ct = default);
}
