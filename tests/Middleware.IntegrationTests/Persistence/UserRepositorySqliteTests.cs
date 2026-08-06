using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Middleware.Core.Domain;
using Middleware.Infrastructure.Persistence;
using Xunit;

namespace Middleware.IntegrationTests.Persistence;

/// <summary>
/// Runnable persistence integration test over an in-memory SQLite database (kept alive by an open
/// connection). Exercises the mapped schema and the repository round-trip without requiring Docker.
/// </summary>
public sealed class UserRepositorySqliteTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public UserRepositorySqliteTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();
    }

    private AppDbContext NewContext() => new(_options);

    [Fact]
    public async Task RoundTripsUserByUsername()
    {
        await using (var db = NewContext())
        {
            await new UserRepository(db).AddAsync(new UserAccount("demo", "hash", Role.User));
        }

        await using (var db = NewContext())
        {
            var repo = new UserRepository(db);
            var found = await repo.FindByUsernameAsync("demo");

            Assert.NotNull(found);
            Assert.Equal("demo", found!.Username);
            Assert.Equal("hash", found.PasswordHash);
            Assert.Equal(Role.User, found.Role);
            Assert.True(await repo.ExistsByUsernameAsync("demo"));
            Assert.False(await repo.ExistsByUsernameAsync("missing"));
        }
    }

    [Fact]
    public async Task EnforcesUniqueUsername()
    {
        await using var db = NewContext();
        var repo = new UserRepository(db);
        await repo.AddAsync(new UserAccount("dup", "h1", Role.User));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repo.AddAsync(new UserAccount("dup", "h2", Role.User)));
    }

    public void Dispose() => _connection.Dispose();
}
