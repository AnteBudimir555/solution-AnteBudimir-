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
    /// The price-filtered candidate set for a filtered query (optionally scoped to a category), from
    /// which the service slices the requested page.
    ///
    /// <para>Only the <em>unfiltered</em> candidate set is cached, keyed by category alone (see
    /// <see cref="CacheKeys.FilterCandidates"/>); the price bounds are applied per call over that
    /// cached set. The upstream call never depended on the bounds, so keying on them bought nothing and
    /// cost a full catalog fetch per distinct pair. Filtering a few hundred already-materialized
    /// products per request is far cheaper than the fetch it replaces.</para>
    /// </summary>
    public async ValueTask<IReadOnlyList<Product>> PriceFilteredCandidatesAsync(
        string? category, decimal? minPrice, decimal? maxPrice, CancellationToken ct = default)
    {
        var candidates = (await CandidatesAsync(category, ct)).Items;

        var filtered = candidates.Where(p => MatchesPrice(p.Price, minPrice, maxPrice)).ToList();
        logger.LogDebug("Price filter [{Min}, {Max}] on {Count} candidates -> {Matches} matches",
            minPrice, maxPrice, candidates.Count, filtered.Count);
        return filtered;
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
        var candidates = normalizedCategory.Length == 0
            ? await source.ListAsync(0, IProductSource.All, ct)
            : await source.FindByCategoryAsync(normalizedCategory, 0, IProductSource.All, ct);

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
