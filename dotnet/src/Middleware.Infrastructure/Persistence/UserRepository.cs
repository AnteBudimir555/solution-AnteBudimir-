using Microsoft.EntityFrameworkCore;

namespace Middleware.Infrastructure.Persistence;

/// <summary>EF Core implementation of <see cref="IUserRepository"/>.</summary>
public sealed class UserRepository(AppDbContext db) : IUserRepository
{
    public Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default) =>
        db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username, ct);

    public Task<bool> ExistsByUsernameAsync(string username, CancellationToken ct = default) =>
        db.Users.AsNoTracking().AnyAsync(u => u.Username == username, ct);

    public async Task AddAsync(UserAccount user, CancellationToken ct = default)
    {
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
    }
}
