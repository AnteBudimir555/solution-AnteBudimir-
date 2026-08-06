using Middleware.Core.Abstractions;
using Middleware.Core.Domain;
using Middleware.Core.Dtos;

namespace Middleware.Core.Services;

/// <summary>
/// Application service over the <see cref="IProductSource"/> abstraction. Responsible for pagination,
/// mapping to API DTOs and the in-service price-range filtering that the upstream cannot do.
///
/// <para>Cache-backed upstream access is delegated to <see cref="ProductQueryCache"/> (a separate class
/// so the single-flight cache is honoured and the price-filter candidate set can be cached
/// independently of pagination); this service handles orchestration, in-service paging and mapping.</para>
///
/// <para>Every read goes through that cache except <see cref="GetByIdAsync"/>, which is uncached on
/// purpose — see the note there. <see cref="IProductSource"/> is still injected for exactly that one
/// call.</para>
/// </summary>
public sealed class ProductService(IProductSource source, IProductQueryCache queries, ProductMapper mapper)
{
    /// <summary>
    /// Paginated trimmed list of all products.
    /// <para>Routed through the cache via the no-category path rather than calling the source
    /// directly. <c>CategoryPageAsync(null, …)</c> issues the identical <c>source.ListAsync</c> call
    /// this method used to make, so the two are the same request — which is the point: an unfiltered
    /// <c>/api/products/filter</c> was already cached, and serving byte-identical data from cache or
    /// not depending only on which URL the client picked was arbitrary. They now share one entry.</para>
    /// </summary>
    public async Task<PagedResponse<ProductSummaryDto>> ListAsync(int page, int size, CancellationToken ct = default)
    {
        var result = await queries.CategoryPageAsync(null, page, size, ct);
        return ToSummaryPage(result.Items, page, size, result.Total);
    }

    /// <summary>
    /// Full detail of a single product.
    /// <para><b>Deliberately uncached</b>, and the only path still going to the source directly. A
    /// product's own record is what a client reads before acting on it — stock and price are the
    /// fields most likely to have moved — so this trades an upstream call for freshness. The listing
    /// endpoints cache because a stale page costs a client little; a stale detail can cost it an
    /// order. Revisit this only alongside a way to invalidate the entry.</para>
    /// </summary>
    public async Task<ProductDetailDto> GetByIdAsync(long id, CancellationToken ct = default)
    {
        var product = await source.GetByIdAsync(id, ct);
        return mapper.ToDetail(product);
    }

    /// <summary>
    /// Filters by category and/or price range (combinable). Category is pushed down to the source;
    /// price range is applied in-service.
    ///
    /// <para>When no price bound is present the source paginates directly (cached per page). When a price
    /// bound is present the full price-filtered candidate set is fetched once (cached by category and
    /// price bounds via <see cref="ProductQueryCache"/>, independent of pagination) and the requested
    /// page is sliced from it here, so paging through the result never re-fetches the upstream catalog.</para>
    /// </summary>
    public async Task<PagedResponse<ProductSummaryDto>> FilterAsync(
        string? category, decimal? minPrice, decimal? maxPrice, int page, int size, CancellationToken ct = default)
    {
        var hasPriceFilter = minPrice is not null || maxPrice is not null;

        if (!hasPriceFilter)
        {
            var result = await queries.CategoryPageAsync(category, page, size, ct);
            return ToSummaryPage(result.Items, page, size, result.Total);
        }

        var filtered = await queries.PriceFilteredCandidatesAsync(category, minPrice, maxPrice, ct);
        var pageItems = Paginate(filtered, page, size);
        return ToSummaryPage(pageItems, page, size, filtered.Count);
    }

    /// <summary>Free-text search by product name (pushed down to the source, cached per page).</summary>
    public async Task<PagedResponse<ProductSummaryDto>> SearchByNameAsync(
        string? query, int page, int size, CancellationToken ct = default)
    {
        var result = await queries.SearchAsync(query, page, size, ct);
        return ToSummaryPage(result.Items, page, size, result.Total);
    }

    /// <summary>Available category identifiers (cached under a long TTL of their own).</summary>
    public async Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct = default) =>
        await queries.CategoriesAsync(ct);

    // --- helpers -----------------------------------------------------------

    private static int Offset(int page, int size) => page * size;

    private static IReadOnlyList<Product> Paginate(IReadOnlyList<Product> items, int page, int size)
    {
        var from = Math.Min(Offset(page, size), items.Count);
        var to = Math.Min(from + size, items.Count);
        return items is List<Product> list ? list.GetRange(from, to - from) : items.Skip(from).Take(to - from).ToList();
    }

    private PagedResponse<ProductSummaryDto> ToSummaryPage(IReadOnlyList<Product> products, int page, int size, long total)
    {
        var summaries = products.Select(mapper.ToSummary).ToList();
        return PagedResponse<ProductSummaryDto>.Of(summaries, page, size, total);
    }
}
