using Middleware.Core.Domain;

namespace Middleware.Core.Services;

/// <summary>
/// Cache-aware access to the product source for the search and filter endpoints. Extracted as an
/// interface so <see cref="ProductService"/> can be unit-tested against a substitute, mirroring the
/// original design where the cache is a distinct collaborator from the orchestrating service.
/// </summary>
public interface IProductQueryCache
{
    /// <summary>One page of name-search results (pagination pushed down to the source).</summary>
    ValueTask<ProductPage> SearchAsync(string? query, int page, int size, CancellationToken ct = default);

    /// <summary>One page of the no-price path: category listing (or the full list), paginated by the source.</summary>
    ValueTask<ProductPage> CategoryPageAsync(string? category, int page, int size, CancellationToken ct = default);

    /// <summary>
    /// One page of a price-filtered query (optionally scoped to a category), with
    /// <see cref="ProductPage.Total"/> counting the whole filtered result rather than the page.
    ///
    /// <para>Which of two strategies runs depends on <c>IProductSource.SupportsPriceFilter</c>, and the
    /// choice is the cache's to make because it decides the key. A source that cannot filter on price
    /// is asked for the unfiltered candidate set, cached by category alone — independent of both
    /// pagination and the bounds — and the bounds are applied in memory over that cached set. A source
    /// that can is asked for the filtered page directly, keyed on the bounds because the answer now
    /// depends on them.</para>
    ///
    /// <para>Returning a page rather than the candidate list keeps that fork invisible to the caller:
    /// under push-down there is no candidate set to hand back, because not fetching one is the point.</para>
    /// </summary>
    /// <exception cref="Middleware.Core.Exceptions.UpstreamException">
    /// if the source returns more candidates than <c>Upstream:MaxInMemoryCandidates</c> allows to be
    /// filtered in memory, or if a source that declared price-filter support returns a result saying it
    /// did not apply one.
    /// </exception>
    ValueTask<ProductPage> PriceFilteredPageAsync(
        string? category, decimal? minPrice, decimal? maxPrice, int page, int size, CancellationToken ct = default);

    /// <summary>
    /// The category identifiers the source exposes, cached under its own long TTL
    /// (<c>Cache:CategoriesExpireAfterWriteSeconds</c>) rather than the general one — the answer
    /// changes approximately never.
    /// </summary>
    ValueTask<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct = default);
}
