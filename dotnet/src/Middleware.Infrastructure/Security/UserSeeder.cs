using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Middleware.Core.Domain;
using Middleware.Core.Options;
using Middleware.Infrastructure.Persistence;

namespace Middleware.Infrastructure.Security;

/// <summary>
/// Creates the configured seed user on startup (when enabled and not already present) so the API can
/// be exercised without a manual registration step. The password is stored only as a BCrypt hash.
///
/// <para>Registered after <see cref="DatabaseInitializer"/> so the schema exists before the insert:
/// hosted services start sequentially in registration order.</para>
/// </summary>
public sealed class UserSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<SeedUserOptions> options,
    IPasswordHasher passwordHasher,
    ILogger<UserSeeder> logger) : IHostedService
{
    private const Role DefaultRole = Role.User;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seed = options.Value;
        if (!seed.Enabled)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        if (await users.ExistsByUsernameAsync(seed.Username, cancellationToken))
        {
            logger.LogInformation("Seed user '{Username}' already exists; skipping.", seed.Username);
            return;
        }

        var user = new UserAccount(seed.Username, passwordHasher.Hash(seed.Password), DefaultRole);
        await users.AddAsync(user, cancellationToken);
        logger.LogInformation("Seeded user '{Username}' with role {Role}.", seed.Username, DefaultRole);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
