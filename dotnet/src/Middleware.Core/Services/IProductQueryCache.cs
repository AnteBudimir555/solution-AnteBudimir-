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
    /// The full, already price-filtered candidate set for a price-filtered query (optionally scoped to
    /// a category). Cached by category + price bounds only — independent of pagination.
    /// </summary>
    ValueTask<IReadOnlyList<Product>> PriceFilteredCandidatesAsync(
        string? category, decimal? minPrice, decimal? maxPrice, CancellationToken ct = default);
}
