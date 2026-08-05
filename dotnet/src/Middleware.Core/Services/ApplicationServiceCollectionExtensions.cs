using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Middleware.Core.Options;

namespace Middleware.Core.Services;

/// <summary>
/// DI wiring for the application layer: binds the summary/cache options, configures the single-flight
/// <see cref="HybridCache"/> (the .NET analog of Spring's Caffeine-backed <c>@Cacheable(sync = true)</c>)
/// with a write TTL from <see cref="CacheOptions"/>, and registers the product services.
/// <para>The upstream <see cref="Middleware.Core.Abstractions.IProductSource"/> is registered separately
/// by the infrastructure layer.</para>
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SummaryOptions>(configuration.GetSection(SummaryOptions.SectionName));
        services.Configure<CacheOptions>(configuration.GetSection(CacheOptions.SectionName));

        var cacheOptions = configuration.GetSection(CacheOptions.SectionName).Get<CacheOptions>() ?? new CacheOptions();
        var ttl = TimeSpan.FromSeconds(cacheOptions.ExpireAfterWriteSeconds);

        services.AddHybridCache(options =>
        {
            // Mirrors the Java Caffeine spec's expireAfterWrite. HybridCache is L1-only here (no
            // distributed backplane configured), so this is the in-memory entry lifetime.
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = ttl,
                LocalCacheExpiration = ttl
            };
        });

        services.AddSingleton<ProductMapper>();
        services.AddScoped<IProductQueryCache, ProductQueryCache>();
        services.AddScoped<ProductService>();

        return services;
    }
}
