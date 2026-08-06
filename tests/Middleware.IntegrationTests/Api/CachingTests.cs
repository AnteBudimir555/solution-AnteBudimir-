using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Middleware.Core.Abstractions;
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
    /// The analog of the Java test's <c>clearCaches</c>. This used to evict each entry by its exact
    /// key, because the comment here claimed HybridCache exposed no clear-all on this target — it does
    /// (see <see cref="MiddlewareApiFactory.ClearCachesAsync"/>), and the key list was both
    /// unnecessary and a trap: a test touching a key nobody remembered to add would silently inherit
    /// the previous test's entry.
    /// </summary>
    public async Task InitializeAsync()
    {
        factory.Source.ClearSubstitute(ClearOptions.All);
        await factory.ClearCachesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RepeatedSearchHitsUpstreamOnce()
    {
        factory.Source.QueryAsync(SearchQuery("phone", 0, 20), Arg.Any<CancellationToken>())
            .Returns(new ProductQueryResult(new ProductPage([], 0, 0, 20), false));

        await InScopeAsync(q => q.SearchAsync("phone", 0, 20));
        await InScopeAsync(q => q.SearchAsync("phone", 0, 20));

        await factory.Source.Received(1).QueryAsync(SearchQuery("phone", 0, 20), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchInputsThatNormalizeToTheSameKeyShareOneUpstreamCall()
    {
        factory.Source.QueryAsync(SearchQuery("phone", 0, 20), Arg.Any<CancellationToken>())
            .Returns(new ProductQueryResult(new ProductPage([], 0, 0, 20), false));

        await InScopeAsync(q => q.SearchAsync("Phone", 0, 20));
        await InScopeAsync(q => q.SearchAsync("  phone  ", 0, 20));

        await factory.Source.Received(1).QueryAsync(SearchQuery("phone", 0, 20), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PriceFilterCandidateSetIsFetchedOncePerCategoryWhateverTheBounds()
    {
        factory.Source.QueryAsync(CandidateQuery(), Arg.Any<CancellationToken>())
            .Returns(new ProductQueryResult(new ProductPage([], 0, 0, 0), false));

        await InScopeAsync(q => q.PriceFilteredPageAsync(null, 10m, 50m, 0, 20));
        // Any other range over the same category must reuse the cached candidate set. The bounds are
        // not part of the key precisely so that a client varying them cannot force a catalog fetch
        // per request — they are applied in memory over this one entry.
        await InScopeAsync(q => q.PriceFilteredPageAsync(null, 10.00m, 50.0m, 0, 20));
        await InScopeAsync(q => q.PriceFilteredPageAsync(null, 11.37m, 49.99m, 1, 20));
        await InScopeAsync(q => q.PriceFilteredPageAsync(null, null, null, 0, 20));

        await factory.Source.Received(1).QueryAsync(CandidateQuery(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// MIGRATION_PLAN S3, over the real host rather than a substituted collaborator: the plain listing
    /// and an unfiltered <c>/filter</c> are the same upstream request, so they must share one entry.
    /// Before this they did not — identical bytes were served from cache or fetched afresh depending
    /// only on which URL the client happened to pick.
    /// </summary>
    [Fact]
    public async Task TheListingAndAnUnfilteredFilterShareOneUpstreamCall()
    {
        factory.Source.QueryAsync(PageQuery(null, 0, 20), Arg.Any<CancellationToken>())
            .Returns(new ProductQueryResult(new ProductPage([], 0, 0, 20), false));

        await InScopeAsync(q => q.CategoryPageAsync(null, 0, 20));
        await InScopeAsync(q => q.CategoryPageAsync(null, 0, 20));

        await factory.Source.Received(1).QueryAsync(PageQuery(null, 0, 20), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The category list is cached under a TTL of its own, so the endpoint stops asking the upstream
    /// on every request for an answer that changes approximately never.
    /// </summary>
    [Fact]
    public async Task RepeatedCategoryListingHitsUpstreamOnce()
    {
        factory.Source.CategoriesAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)new[] { "beauty", "laptops" });

        var first = await InScopeAsync(q => q.CategoriesAsync());
        var second = await InScopeAsync(q => q.CategoriesAsync());

        Assert.Equal(new[] { "beauty", "laptops" }, first);
        Assert.Equal(new[] { "beauty", "laptops" }, second);
        await factory.Source.Received(1).CategoriesAsync(Arg.Any<CancellationToken>());
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
        factory.Source.QueryAsync(SearchQuery("bulky", 0, 100), Arg.Any<CancellationToken>())
            .Returns(new ProductQueryResult(new ProductPage(products, products.Count, 0, 100), false));

        await InScopeAsync(q => q.SearchAsync("bulky", 0, 100));
        await InScopeAsync(q => q.SearchAsync("bulky", 0, 100));

        await factory.Source.Received(1).QueryAsync(SearchQuery("bulky", 0, 100), Arg.Any<CancellationToken>());
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

    // --- the three query shapes the cache issues -----------------------------

    private static ProductQuery SearchQuery(string name, int skip, int limit) =>
        new() { NameContains = name, Skip = skip, Limit = limit };

    private static ProductQuery PageQuery(string? category, int skip, int limit) =>
        new() { Category = category, Skip = skip, Limit = limit };

    /// <summary>
    /// The unbounded, unfiltered fetch behind the in-memory price path. Written out rather than matched
    /// loosely because the absence of the price bounds is the property under test: they must not reach
    /// the source, or the call would depend on something the key does not.
    /// </summary>
    private static ProductQuery CandidateQuery(string? category = null) =>
        new() { Category = category, Limit = null };
}
