using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Middleware.Core.Abstractions;
using Middleware.Core.Common;
using Middleware.Core.Domain;
using Middleware.Core.Services;
using Middleware.IntegrationTests.Support;
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
            CacheKeys.Search("bulky", 0, 100),
            CacheKeys.FilterCandidates(null)
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
    public async Task PriceFilterCandidateSetIsFetchedOncePerCategoryWhateverTheBounds()
    {
        factory.Source.ListAsync(0, IProductSource.All, Arg.Any<CancellationToken>())
            .Returns(new ProductPage([], 0, 0, 0));

        await InScopeAsync(q => q.PriceFilteredCandidatesAsync(null, 10m, 50m));
        // Any other range over the same category must reuse the cached candidate set. The bounds are
        // not part of the key precisely so that a client varying them cannot force a catalog fetch
        // per request — they are applied in memory over this one entry.
        await InScopeAsync(q => q.PriceFilteredCandidatesAsync(null, 10.00m, 50.0m));
        await InScopeAsync(q => q.PriceFilteredCandidatesAsync(null, 11.37m, 49.99m));
        await InScopeAsync(q => q.PriceFilteredCandidatesAsync(null, null, null));

        await factory.Source.Received(1).ListAsync(0, IProductSource.All, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Guards the unit of the configured cache size bound. <c>MemoryCacheOptions.SizeLimit</c> is a
    /// <em>byte</em> budget — HybridCache sizes each entry by its serialized length — but the Caffeine
    /// spec it replaces counted entries. A limit carried across as though it were an entry count is a
    /// few hundred bytes, which throws nothing and logs nothing: it silently retains no entry large
    /// enough to matter, turning every request into a cache miss.
    ///
    /// <para>This is why the fixture is a realistically-sized page rather than the empty one the other
    /// tests use. An empty page fits inside even a broken limit, so the tests above would stay green
    /// while the cache did nothing in production — which is exactly how such a bound gets shipped.</para>
    /// </summary>
    [Fact]
    public async Task AFullSizedPageIsRetainedUnderTheConfiguredSizeBound()
    {
        var products = Enumerable.Range(1, 100)
            .Select(i => TestData.Product(i, $"Product {i}", new string('d', 400), 9.99m, "beauty"))
            .ToList();
        factory.Source.SearchByNameAsync("bulky", 0, 100, Arg.Any<CancellationToken>())
            .Returns(new ProductPage(products, products.Count, 0, 100));

        await InScopeAsync(q => q.SearchAsync("bulky", 0, 100));
        await InScopeAsync(q => q.SearchAsync("bulky", 0, 100));

        await factory.Source.Received(1).SearchByNameAsync("bulky", 0, 100, Arg.Any<CancellationToken>());
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
