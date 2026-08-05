using Microsoft.EntityFrameworkCore;
using Middleware.Core.Domain;
using Middleware.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace Middleware.IntegrationTests.Persistence;

/// <summary>
/// Feature-parity acceptance test for Phase 2: the generated <c>InitialCreate</c> migration applies
/// cleanly to a real PostgreSQL instance and the repository round-trips a user. Requires Docker; when
/// it is unavailable (e.g. this dev box) the test reports as skipped rather than failing, and runs for
/// real in CI where Docker is present.
/// </summary>
public class PostgresMigrationTests
{
    [SkippableFact]
    public async Task AppliesInitialCreateMigrationAndRoundTripsUser()
    {
        // Build() validates the Docker endpoint and StartAsync() launches the container; both require
        // Docker, so guard both and convert unavailability into a skip. Assertions below stay outside
        // this guard so a genuine migration/round-trip failure surfaces as a real failure.
        PostgreSqlContainer container;
        try
        {
            container = new PostgreSqlBuilder("postgres:17").Build();
            await container.StartAsync();
        }
        catch (Exception ex)
        {
            throw new SkipException("Docker/Testcontainers unavailable: " + ex.Message);
        }

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(container.GetConnectionString())
                .Options;

            await using (var db = new AppDbContext(options))
            {
                await db.Database.MigrateAsync(); // applies InitialCreate against real Postgres
            }

            await using (var db = new AppDbContext(options))
            {
                var repo = new UserRepository(db);
                await repo.AddAsync(new UserAccount("demo", "hash", Role.User));

                var found = await repo.FindByUsernameAsync("demo");
                Assert.NotNull(found);
                Assert.Equal("demo", found!.Username);
                Assert.Equal(Role.User, found.Role);
            }
        }
        finally
        {
            await container.DisposeAsync();
        }
    }
}
