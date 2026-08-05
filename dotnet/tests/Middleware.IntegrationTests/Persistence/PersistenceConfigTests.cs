using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Middleware.Infrastructure.Persistence;
using Xunit;

namespace Middleware.IntegrationTests.Persistence;

/// <summary>
/// Verifies provider selection and the fail-fast contract of <see cref="PersistenceServiceCollectionExtensions.AddPersistence"/>.
/// </summary>
public class PersistenceConfigTests
{
    [Fact]
    public void PostgresProviderWithoutConnectionStringThrows()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Provider"] = "Postgres" })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddPersistence(config));
        Assert.Contains("Default", ex.Message);
    }

    [Fact]
    public void PostgresProviderWithConnectionStringRegisters()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Postgres",
                ["ConnectionStrings:Default"] = "Host=localhost;Database=middleware;Username=u;Password=p"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddPersistence(config); // must not throw

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IUserRepository>());
    }

    [Fact]
    public void DefaultsToSqliteWhenProviderUnset()
    {
        IConfiguration config = new ConfigurationBuilder().Build();

        var services = new ServiceCollection();
        services.AddPersistence(config); // no throw and no connection string required

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
