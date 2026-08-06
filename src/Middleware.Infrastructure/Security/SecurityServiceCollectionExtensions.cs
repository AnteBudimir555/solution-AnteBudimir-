using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Middleware.Core.Options;

namespace Middleware.Infrastructure.Security;

/// <summary>
/// DI wiring for the security building blocks: token issuing/validation, password hashing and the
/// startup user seeder.
///
/// <para><see cref="JwtOptions"/> is bound with <c>ValidateOnStart</c>, which reproduces the original
/// service's fail-fast: a missing or too-short signing secret aborts start-up instead of surfacing as
/// a runtime failure on the first login. There is deliberately no default secret.</para>
///
/// <para><see cref="SeedUserOptions"/> is validated the same way, so enabling the seeder without
/// credentials aborts start-up rather than creating an account with a blank password.</para>
/// </summary>
public static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddSecurityServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<SeedUserOptions>()
            .Bind(configuration.GetSection(SeedUserOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<JwtService>();
        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.AddHostedService<UserSeeder>();

        return services;
    }
}
