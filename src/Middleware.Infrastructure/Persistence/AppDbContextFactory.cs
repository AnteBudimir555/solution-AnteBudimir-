using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Middleware.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used only by <c>dotnet ef</c> to build the context when generating migrations.
/// Migrations are generated for Npgsql — the production target — so the schema artifact matches the
/// deployed database. The development SQLite database carries no migration history and is created via
/// <c>EnsureCreated</c> at startup instead (see <see cref="PersistenceServiceCollectionExtensions"/>).
/// The connection string here is a placeholder: no database is contacted to scaffold a migration.
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=middleware;Username=postgres;Password=postgres")
            .Options;
        return new AppDbContext(options);
    }
}
