using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Middleware.Infrastructure.Persistence;

/// <summary>
/// Composition-root wiring for the persistence layer: provider selection (Postgres for production-like
/// deployments, SQLite for local development — the analogs of the original <c>postgres</c>/<c>dev</c>
/// profiles), the repository registration, and startup schema initialization.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    public const string PostgresProvider = "Postgres";
    public const string SqliteProvider = "Sqlite";

    /// <summary>
    /// Registers <see cref="AppDbContext"/> and <see cref="IUserRepository"/>. The provider is chosen
    /// by <c>Database:Provider</c> (default <see cref="SqliteProvider"/>). Under Postgres a
    /// <c>Default</c> connection string is <em>required</em> and its absence throws at startup — there
    /// is no silent fallback, matching the original service's fail-fast on missing DB credentials.
    /// </summary>
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        string provider = configuration["Database:Provider"] ?? SqliteProvider;

        if (string.Equals(provider, PostgresProvider, StringComparison.OrdinalIgnoreCase))
        {
            string connectionString = configuration.GetConnectionString("Default")
                ?? throw new InvalidOperationException(
                    "No 'Default' connection string is configured for the Postgres provider. Set "
                    + "ConnectionStrings:Default (e.g. via DB_URL/credentials); there is no fallback.");
            services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
        }
        else
        {
            // Local development: a self-contained SQLite file (the analog of the embedded H2 dev DB).
            string connectionString = configuration.GetConnectionString("Default")
                ?? "Data Source=middleware-dev.db";
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString));
        }

        services.AddScoped<IUserRepository, UserRepository>();

        // Registered here, by the module that owns the schema, so it always starts before any hosted
        // service that reads or writes user data — hosted services start in registration order, and
        // persistence is wired ahead of the layers that depend on it.
        services.AddHostedService<DatabaseInitializer>();

        return services;
    }

    /// <summary>
    /// Applies the schema at startup: <c>Migrate</c> for the relational Postgres target (applying the
    /// generated migrations), <c>EnsureCreated</c> for the throwaway SQLite dev database (which carries
    /// no migration history).
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.Database.IsNpgsql())
        {
            await db.Database.MigrateAsync(ct);
        }
        else
        {
            await db.Database.EnsureCreatedAsync(ct);
        }
    }
}
