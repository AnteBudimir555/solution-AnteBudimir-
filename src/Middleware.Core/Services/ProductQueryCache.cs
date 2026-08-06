using System.Collections.Immutable;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Middleware.Core.Abstractions;
using Middleware.Core.Common;
using Middleware.Core.Domain;
using Middleware.Core.Exceptions;
using Middleware.Core.Options;

namespace Middleware.Core.Services;

/// <summary>
/// Cache-aware access to the <see cref="IProductSource"/> for the search and filter endpoints.
///
/// <para>The caching lives in its own class, separate from <c>ProductService</c>, for two reasons:</para>
/// <list type="bullet">
///   <item><see cref="HybridCache"/> provides single-flight (stampede) protection per key — the direct
///   equivalent of Spring's <c>@Cacheable(sync = true)</c>: concurrent callers for the same key share
///   one upstream fetch.</item>
///   <item>The candidate set behind a price filter is cached by category alone —
///   <see cref="PriceFilteredCandidatesAsync"/> is deliberately independent of both pagination and the
///   price bounds — so paging through a filtered result, or filtering the same category by any other
///   range, reuses a single upstream fetch instead of re-fetching the full catalog.</item>
/// </list>
///
/// <para>Each method normalizes its free-text inputs via <see cref="CacheKeys"/>, the same
/// normalization the cache keys apply, so the cache key and the actual upstream call can never diverge.
/// Nothing that does <em>not</em> change the upstream call belongs in a key: the price bounds are
/// applied in memory, over the cached set, and so are absent from it.</para>
/// </summary>
public sealed class ProductQueryCache(
    IProductSource source,
    HybridCache cache,
    IOptions<UpstreamOptions> options,
    IOptions<CacheOptions> cacheOptions,
    ILogger<ProductQueryCache> logger) : IProductQueryCache
{
    private readonly int _maxInMemoryCandidates = options.Value.MaxInMemoryCandidates;

    /// <summary>The one entry that opts out of the registered default TTL — see <see cref="CategoriesAsync"/>.</summary>
    private readonly HybridCacheEntryOptions _categoriesEntryOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(cacheOptions.Value.CategoriesExpireAfterWriteSeconds),
        LocalCacheExpiration = TimeSpan.FromSeconds(cacheOptions.Value.CategoriesExpireAfterWriteSeconds)
    };

    /// <summary>One page of name-search results (pagination pushed down to the source).</summary>
    public async ValueTask<ProductPage> SearchAsync(string? query, int page, int size, CancellationToken ct = default)
    {
        var normalizedQuery = CacheKeys.NormalizeText(query);
        return await cache.GetOrCreateAsync(
            CacheKeys.Search(query, page, size),
            (source, normalizedQuery, page, size),
            static async (state, token) =>
                (await state.source.QueryAsync(
                    new ProductQuery
                    {
                        NameContains = state.normalizedQuery,
                        Skip = Offset(state.page, state.size),
                        Limit = state.size
                    },
                    token)).Page,
            cancellationToken: ct);
    }

    /// <summary>One page of the no-price path: category listing (or the full list), paginated by the source.</summary>
    public async ValueTask<ProductPage> CategoryPageAsync(string? category, int page, int size, CancellationToken ct = default)
    {
        var normalizedCategory = CacheKeys.NormalizeText(category);
        return await cache.GetOrCreateAsync(
            CacheKeys.FilterPage(category, page, size),
            (source, normalizedCategory, page, size),
            static async (state, token) =>
                (await state.source.QueryAsync(
                    new ProductQuery
                    {
                        Category = NullIfEmpty(state.normalizedCategory),
                        Skip = Offset(state.page, state.size),
                        Limit = state.size
                    },
                    token)).Page,
            cancellationToken: ct);
    }

    /// <summary>
    /// One page of a price-filtered query, by whichever of the two strategies the source's declared
    /// capability calls for.
    ///
    /// <para>The fork is here, and not in <see cref="ProductService"/>, because it is a decision about
    /// <em>how to ask and how to key</em>, which is this class's job. It has to be made before the
    /// call: a cache key must cover exactly what the call depends on, so whether the bounds belong in
    /// the key cannot be discovered from the answer.</para>
    /// </summary>
    public async ValueTask<ProductPage> PriceFilteredPageAsync(
        string? category, decimal? minPrice, decimal? maxPrice, int page, int size, CancellationToken ct = default) =>
        source.SupportsPriceFilter
            ? await SourceFilteredPageAsync(category, minPrice, maxPrice, page, size, ct)
            : await InMemoryFilteredPageAsync(category, minPrice, maxPrice, page, size, ct);

    /// <summary>
    /// The path DummyJSON takes: fetch the unfiltered candidate set once per category, then filter and
    /// paginate it here.
    ///
    /// <para>Only the <em>unfiltered</em> set is cached, keyed by category alone (see
    /// <see cref="CacheKeys.FilterCandidates"/>); the bounds are applied per call over it. The upstream
    /// call never depended on the bounds, so keying on them bought nothing and cost a full catalog
    /// fetch per distinct pair. Filtering a few hundred already-materialized products per request is
    /// far cheaper than the fetch it replaces.</para>
    /// </summary>
    private async ValueTask<ProductPage> InMemoryFilteredPageAsync(
        string? category, decimal? minPrice, decimal? maxPrice, int page, int size, CancellationToken ct)
    {
        var candidates = (await CandidatesAsync(category, ct)).Items;

        var filtered = candidates.Where(p => MatchesPrice(p.Price, minPrice, maxPrice)).ToList();
        logger.LogDebug("Price filter [{Min}, {Max}] on {Count} candidates -> {Matches} matches",
            minPrice, maxPrice, candidates.Count, filtered.Count);

        var offset = Offset(page, size);
        var from = Math.Min(offset, filtered.Count);
        var to = Math.Min(from + size, filtered.Count);
        // Frozen because ProductPage is [ImmutableObject(true)] and that promise is structural, not
        // circumstantial (T4) — even for a page this method builds fresh and never caches.
        return new ProductPage(
            ImmutableArray.CreateRange(filtered.GetRange(from, to - from)), filtered.Count, offset, size);
    }

    /// <summary>
    /// The path a price-capable source takes: one query carrying the bounds, keyed on them because the
    /// answer depends on them, with no candidate set fetched or held at all.
    ///
    /// <para>No source in this repository takes it. It exists because the alternative is an interface
    /// that can express a capability nothing ever acts on, which is how <c>Cache:MaximumSize</c> came
    /// to be documented for months without being read (B3). <c>PriceFilterApplied</c> is what makes the
    /// promise checkable, and it is checked below.</para>
    /// </summary>
    private async ValueTask<ProductPage> SourceFilteredPageAsync(
        string? category, decimal? minPrice, decimal? maxPrice, int page, int size, CancellationToken ct)
    {
        var normalizedCategory = CacheKeys.NormalizeText(category);
        return await cache.GetOrCreateAsync(
            CacheKeys.PricePage(category, minPrice, maxPrice, page, size),
            (self: this, normalizedCategory, minPrice, maxPrice, page, size),
            static (state, token) => state.self.FetchSourceFilteredAsync(
                state.normalizedCategory, state.minPrice, state.maxPrice, state.page, state.size, token),
            cancellationToken: ct);
    }

    private async ValueTask<ProductPage> FetchSourceFilteredAsync(
        string normalizedCategory, decimal? minPrice, decimal? maxPrice, int page, int size, CancellationToken ct)
    {
        var result = await source.QueryAsync(
            new ProductQuery
            {
                Category = NullIfEmpty(normalizedCategory),
                MinPrice = minPrice,
                MaxPrice = maxPrice,
                Skip = Offset(page, size),
                Limit = size
            },
            ct);

        if (!result.PriceFilterApplied)
        {
            // The source declared the capability and then declined to use it. The page it returned was
            // paginated before any filtering, so neither the items nor the total can be repaired here —
            // filtering them now would silently return a short page and an inflated count. Refusing is
            // the same judgement S5 makes about the in-memory threshold: a wrong answer with a 200 is
            // worse than a 502.
            logger.LogError(
                "Source declares SupportsPriceFilter but returned PriceFilterApplied=false for "
                + "[{Min}, {Max}]; the page cannot be trusted.", minPrice, maxPrice);
            throw new UpstreamException(
                "The product source declares price-filter support but did not apply the price filter.");
        }

        return result.Page;
    }

    /// <summary>
    /// The cached, unfiltered candidate set for a category (or the whole catalog when blank).
    /// <para>Cached as the <see cref="ProductPage"/> the source returned rather than its
    /// <c>Items</c>: the cache can only skip a deserialize per hit for a type it knows is immutable,
    /// and that knowledge is carried by the concrete record, not by the <c>IReadOnlyList&lt;T&gt;</c>
    /// interface — which nothing could vouch for.</para>
    /// </summary>
    private async ValueTask<ProductPage> CandidatesAsync(string? category, CancellationToken ct)
    {
        var normalizedCategory = CacheKeys.NormalizeText(category);
        return await cache.GetOrCreateAsync(
            CacheKeys.FilterCandidates(category),
            (self: this, normalizedCategory),
            static (state, token) => state.self.FetchCandidatesAsync(state.normalizedCategory, token),
            cancellationToken: ct);
    }

    private async ValueTask<ProductPage> FetchCandidatesAsync(string normalizedCategory, CancellationToken ct)
    {
        // The bounds are deliberately absent from this query, not merely ignored by the source: the
        // call must depend on exactly what the key depends on, and the key is the category alone.
        var candidates = (await source.QueryAsync(
            new ProductQuery { Category = NullIfEmpty(normalizedCategory), Limit = null }, ct)).Page;

        var candidateCount = candidates.Items.Count;
        if (candidateCount > _maxInMemoryCandidates)
        {
            // Price filtering cannot be pushed down to the current source, so the full candidate set
            // would have to be held in memory. Refusing is the safe answer: the alternative is an
            // unbounded allocation driven by upstream catalog size, on a request a client can repeat.
            // A source large enough to reach this needs the price filter pushed down (see the
            // ProductQuery sketch in the migration plan), not a bigger threshold.
            logger.LogError(
                "Price filter would load {Count} candidates into memory, over the {Threshold} limit; refusing. "
                + "Push the price filter down to the source or raise Upstream:MaxInMemoryCandidates deliberately.",
                candidateCount, _maxInMemoryCandidates);
            throw new UpstreamException(
                $"The product source returned {candidateCount} candidates for an in-memory price filter, "
                + $"over the configured limit of {_maxInMemoryCandidates}.");
        }

        return candidates;
    }

    /// <summary>
    /// The category list, cached under a long TTL of its own.
    ///
    /// <para>Cached as a <see cref="CategoryList"/> rather than the bare collection for the same reason
    /// <see cref="CandidatesAsync"/> caches a <see cref="ProductPage"/>: only a concrete type carrying
    /// <c>[ImmutableObject(true)]</c> lets the cache return the stored instance instead of deserializing
    /// on every hit. The names are frozen on the way in, because that attribute is a promise.</para>
    /// </summary>
    public async ValueTask<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct = default)
    {
        var cached = await cache.GetOrCreateAsync(
            CacheKeys.Categories,
            source,
            static async (src, token) =>
                new CategoryList(ImmutableArray.CreateRange(await src.CategoriesAsync(token))),
            _categoriesEntryOptions,
            cancellationToken: ct);
        return cached.Names;
    }

    private static int Offset(int page, int size) => page * size;

    /// <summary>Blank normalizes to "", which the query object spells as "no filter" — <c>null</c>.</summary>
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static bool MatchesPrice(decimal? price, decimal? min, decimal? max)
    {
        if (price is null)
        {
            return false;
        }
        if (min is not null && price < min)
        {
            return false;
        }
        return max is null || price <= max;
    }
}
