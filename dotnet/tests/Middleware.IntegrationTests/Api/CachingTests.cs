using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Middleware.Core.Abstractions;
using Middleware.Core.Common;
using Middleware.Core.Domain;
using Middleware.Core.Services;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace Middleware.IntegrationTests.Api;

/// <summary>
/// Integration test proving the cache boundary actually deduplicates upstream work, ported from
/// <c>CachingIT</c>. Where the Java test relies on Spring's <c>@Cacheable(sync = true)</c> proxy, this
/// one resolves the real <see cref="IProductQueryCache"/> out of the running host — so the DI-registered
/// singleton <see cref="HybridCache"/> and its configured TTL are the ones under test, not a hand-built
/// cache as in the unit suite. Repeated equivalent queries must reach the source only once, and inputs
/// that normalize to the same key (case/whitespace, equivalent numeric scales) must share that call.
/// </summary>
public sealed class CachingTests(MiddlewareApiFactory factory)
    : IClassFixture<MiddlewareApiFactory>, IAsyncLifetime
{
    /// <summary>
    /// The analog of the Java test's <c>clearCaches</c>. HybridCache on this target framework exposes no
    /// clear-all (tag-based eviction arrived in .NET 9), so each entry these tests touch is evicted by
    /// its exact key — built through <see cref="CacheKeys"/>, the same way the cache builds it.
    /// </summary>
    public async Task InitializeAsync()
    {
        factory.Source.ClearSubstitute(ClearOptions.All);

        var cache = factory.Services.GetRequiredService<HybridCache>();
        await cache.RemoveAsync(
        [
            CacheKeys.Search("phone", 0, 20),
            CacheKeys.FilterCandidates(null, 10m, 50m)
        ]);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RepeatedSearchHitsUpstreamOnce()
    {
        factory.Source.SearchByNameAsync("phone", 0, 20, Arg.Any<CancellationToken>())
            .Returns(new ProductPage([], 0, 0, 20));

        await InScopeAsync(q => q.SearchAsync("phone", 0, 20));
        await InScopeAsync(q => q.SearchAsync("phone", 0, 20));

        await factory.Source.Received(1).SearchByNameAsync("phone", 0, 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchInputsThatNormalizeToTheSameKeyShareOneUpstreamCall()
    {
        factory.Source.SearchByNameAsync("phone", 0, 20, Arg.Any<CancellationToken>())
            .Returns(new ProductPage([], 0, 0, 20));

        await InScopeAsync(q => q.SearchAsync("Phone", 0, 20));
        await InScopeAsync(q => q.SearchAsync("  phone  ", 0, 20));

        await factory.Source.Received(1).SearchByNameAsync("phone", 0, 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PriceFilterCandidateSetIsFetchedOncePerCategoryAndBounds()
    {
        factory.Source.ListAsync(0, IProductSource.All, Arg.Any<CancellationToken>())
            .Returns(new ProductPage([], 0, 0, 0));

        await InScopeAsync(q => q.PriceFilteredCandidatesAsync(null, 10m, 50m));
        // Equivalent numeric scale must reuse the cached candidate set (no second upstream fetch).
        await InScopeAsync(q => q.PriceFilteredCandidatesAsync(null, 10.00m, 50.0m));

        await factory.Source.Received(1).ListAsync(0, IProductSource.All, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Runs one query against a cache resolved from a fresh scope, mirroring how a request obtains it.
    /// The scoped wrapper is new each time; the <see cref="HybridCache"/> behind it is the host
    /// singleton, which is what makes deduplication across separate calls meaningful.
    /// </summary>
    private async Task<T> InScopeAsync<T>(Func<IProductQueryCache, ValueTask<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<IProductQueryCache>());
    }
}
