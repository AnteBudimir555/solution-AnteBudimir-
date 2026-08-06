using Middleware.Core.Domain;

namespace Middleware.Core.Abstractions;

/// <summary>
/// Abstraction over a product source — the central extension point of the middleware. The current
/// implementation talks to the DummyJSON REST API, but another backing store can be added simply by
/// providing another implementation; no consumer (service/endpoint) needs to change.
/// <para>Pagination convention: <c>skip</c> is a zero-based offset and <c>limit</c> is the page size;
/// a <c>limit</c> of <see cref="All"/> requests <em>all</em> matching items (used by the service when
/// it needs the full result set to apply in-service filtering before paginating).</para>
/// </summary>
public interface IProductSource
{
    /// <summary><c>limit</c> value requesting <em>all</em> matching items (see the pagination convention above).</summary>
    public const int All = 0;

    /// <summary>Returns a page of all products.</summary>
    Task<ProductPage> ListAsync(int skip, int limit, CancellationToken ct = default);

    /// <summary>Returns the full detail of a single product.</summary>
    /// <exception cref="Exceptions.ProductNotFoundException">if no product has that id</exception>
    Task<Product> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>Returns a page of products belonging to the given category.</summary>
    Task<ProductPage> FindByCategoryAsync(string category, int skip, int limit, CancellationToken ct = default);

    /// <summary>Returns a page of products whose name/title matches the free-text query.</summary>
    Task<ProductPage> SearchByNameAsync(string query, int skip, int limit, CancellationToken ct = default);

    /// <summary>Returns the list of available category identifiers.</summary>
    Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct = default);
}
