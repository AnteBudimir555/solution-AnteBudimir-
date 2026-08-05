using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Middleware.Core.Abstractions;
using Middleware.Core.Common;
using Middleware.Core.Domain;
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
///   <item>The price-filter candidate set is cached by category + price bounds only —
///   <see cref="PriceFilteredCandidatesAsync"/> is deliberately independent of pagination — so paging
///   through a filtered result reuses a single upstream fetch instead of re-fetching the full catalog
///   per page.</item>
/// </list>
///
/// <para>Each method normalizes its free-text inputs via <see cref="CacheKeys"/>, the same
/// normalization the cache keys apply, so the cache key and the actual upstream call can never diverge.</para>
/// </summary>
public sealed class ProductQueryCache(
    IProductSource source,
    HybridCache cache,
    IOptions<UpstreamOptions> options,
    ILogger<ProductQueryCache> logger) : IProductQueryCache
{
    private readonly int _maxInMemoryCandidates = options.Value.MaxInMemoryCandidates;

    /// <summary>One page of name-search results (pagination pushed down to the source).</summary>
    public async ValueTask<ProductPage> SearchAsync(string? query, int page, int size, CancellationToken ct = default)
    {
        var normalizedQuery = CacheKeys.NormalizeText(query);
        return await cache.GetOrCreateAsync(
            CacheKeys.Search(query, page, size),
            (source, normalizedQuery, page, size),
            static (state, token) =>
                new ValueTask<ProductPage>(
                    state.source.SearchByNameAsync(state.normalizedQuery, Offset(state.page, state.size), state.size, token)),
            cancellationToken: ct);
    }

    /// <summary>One page of the no-price path: category listing (or the full list), paginated by the source.</summary>
    public async ValueTask<ProductPage> CategoryPageAsync(string? category, int page, int size, CancellationToken ct = default)
    {
        var normalizedCategory = CacheKeys.NormalizeText(category);
        return await cache.GetOrCreateAsync(
            CacheKeys.FilterPage(category, page, size),
            (source, normalizedCategory, page, size),
            static (state, token) =>
            {
                var offset = Offset(state.page, state.size);
                var task = state.normalizedCategory.Length == 0
                    ? state.source.ListAsync(offset, state.size, token)
                    : state.source.FindByCategoryAsync(state.normalizedCategory, offset, state.size, token);
                return new ValueTask<ProductPage>(task);
            },
            cancellationToken: ct);
    }

    /// <summary>
    /// The full candidate set for a price-filtered query (optionally scoped to a category), already
    /// price-filtered. Cached by category + price bounds only — not by pagination — so the service can
    /// slice any page out of it without another upstream call.
    /// </summary>
    public async ValueTask<IReadOnlyList<Product>> PriceFilteredCandidatesAsync(
        string? category, decimal? minPrice, decimal? maxPrice, CancellationToken ct = default)
    {
        return await cache.GetOrCreateAsync(
            CacheKeys.FilterCandidates(category, minPrice, maxPrice),
            (self: this, category, minPrice, maxPrice),
            static (state, token) => state.self.FetchPriceFilteredAsync(state.category, state.minPrice, state.maxPrice, token),
            cancellationToken: ct);
    }

    private async ValueTask<IReadOnlyList<Product>> FetchPriceFilteredAsync(
        string? category, decimal? minPrice, decimal? maxPrice, CancellationToken ct)
    {
        var normalizedCategory = CacheKeys.NormalizeText(category);
        var candidates = normalizedCategory.Length == 0
            ? await source.ListAsync(0, IProductSource.All, ct)
            : await source.FindByCategoryAsync(normalizedCategory, 0, IProductSource.All, ct);

        var candidateCount = candidates.Items.Count;
        if (candidateCount > _maxInMemoryCandidates)
        {
            // Price filtering cannot be pushed down to the current source, so the full candidate set is
            // held in memory. This is safe for DummyJSON's small catalog; a larger source should push the
            // price filter down or bound the fetch rather than materialize everything here.
            logger.LogWarning(
                "Price filter loaded {Count} candidates into memory (threshold {Threshold}); consider pushing the "
                + "price filter down to the source or bounding the fetch.", candidateCount, _maxInMemoryCandidates);
        }

        var filtered = candidates.Items.Where(p => MatchesPrice(p.Price, minPrice, maxPrice)).ToList();
        logger.LogDebug("Price filter [{Min}, {Max}] on {Count} candidates -> {Matches} matches",
            minPrice, maxPrice, candidateCount, filtered.Count);
        return filtered;
    }

    private static int Offset(int page, int size) => page * size;

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
