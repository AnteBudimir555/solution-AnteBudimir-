using Microsoft.Extensions.Hosting;

namespace Middleware.Infrastructure.Persistence;

/// <summary>
/// Applies the database schema during host start-up — the analog of Hibernate's
/// <c>ddl-auto</c> running as the context is built. Implemented as an <see cref="IHostedService"/>
/// rather than an inline call after <c>WebApplication.Build()</c> so it also runs under
/// <c>WebApplicationFactory</c>, which starts the host without executing the entry point's tail.
///
/// <para>Must be registered before <see cref="Security.UserSeeder"/>: hosted services start
/// sequentially in registration order, so the schema is guaranteed to exist before the seed insert.</para>
/// </summary>
public sealed class DatabaseInitializer(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        services.InitializeDatabaseAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
