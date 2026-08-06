using Middleware.Core.Domain;

namespace Middleware.Core.Abstractions;

/// <summary>
/// Abstraction over a product source — the central extension point of the middleware. The current
/// implementation talks to the DummyJSON REST API, but another backing store can be added simply by
/// providing another implementation; no consumer (service/endpoint) needs to change.
/// <para>Pagination convention: <c>skip</c> is a zero-based offset and <c>limit</c> is the page size;
/// a <c>limit</c> of <see cref="All"/> requests <em>all</em> matching items (used by the service when
/// it needs the full result set to apply in-service filtering before paginating).</para>
///
/// <para><strong>Free-text convention: an implementation must match case-insensitively.</strong> The
/// <c>query</c> and <c>category</c> arguments arrive already normalized — trimmed and lower-cased
/// through <see cref="Common.CacheKeys.NormalizeText"/>, which the caller also uses to build the cache
/// key so that the key and the call it stands for can never diverge. The consequence is a real
/// constraint rather than a formatting note: <em>the caller's original casing is gone by the time it
/// reaches here</em>, so a case-sensitive source would answer a search for <c>iPhone</c> with the
/// results for <c>iphone</c>, and no layer above could detect the substitution. Two requests differing
/// only in case are one request as far as this abstraction is concerned.</para>
///
/// <para>The fold is deliberately invariant-culture, so the same input produces the same call and the
/// same key on any host locale; an implementation must not re-case or re-fold the value against the
/// ambient culture. It is a lower-casing, not full Unicode case folding — so it is not exactly the
/// <see cref="StringComparison.OrdinalIgnoreCase"/> relation, and a source that needs an equality
/// comparer of its own should use <see cref="StringComparison.Ordinal"/> on the value as given.</para>
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
    /// <param name="category">the category identifier, already trimmed and invariant-lower-cased —
    /// see the free-text convention above; it must be matched case-insensitively.</param>
    Task<ProductPage> FindByCategoryAsync(string category, int skip, int limit, CancellationToken ct = default);

    /// <summary>Returns a page of products whose name/title matches the free-text query.</summary>
    /// <param name="query">the search text, already trimmed and invariant-lower-cased — see the
    /// free-text convention above; it must be matched case-insensitively.</param>
    Task<ProductPage> SearchByNameAsync(string query, int skip, int limit, CancellationToken ct = default);

    /// <summary>Returns the list of available category identifiers.</summary>
    Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct = default);
}
