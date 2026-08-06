using Middleware.Core.Domain;

namespace Middleware.Core.Abstractions;

/// <summary>
/// Abstraction over a product source — the central extension point of the middleware. The current
/// implementation talks to the DummyJSON REST API, but another backing store can be added simply by
/// providing another implementation; no consumer (service/endpoint) needs to change.
///
/// <para>Catalog reads go through <see cref="QueryAsync"/>, which takes a <see cref="ProductQuery"/>
/// describing <em>what</em> is wanted and returns a <see cref="ProductQueryResult"/> saying what was
/// actually applied. Fetching one product by id and listing the category names keep signatures of
/// their own: neither is a filter over the catalog, and forcing them through a query object would
/// describe them less clearly, not more.</para>
///
/// <para>Pagination convention: <see cref="ProductQuery.Skip"/> is a zero-based offset and
/// <see cref="ProductQuery.Limit"/> is the page size, with <c>null</c> meaning "every match". A limit
/// of zero or less is not a request for everything — see the note on that member — and an
/// implementation should reject it.</para>
///
/// <para><strong>Free-text convention: an implementation must match case-insensitively.</strong>
/// <see cref="ProductQuery.NameContains"/> and <see cref="ProductQuery.Category"/> arrive already
/// normalized — trimmed and lower-cased through <see cref="Common.CacheKeys.NormalizeText"/>, which
/// the caller also uses to build the cache key so that the key and the call it stands for can never
/// diverge. The consequence is a real constraint rather than a formatting note: <em>the caller's
/// original casing is gone by the time it reaches here</em>, so a case-sensitive source would answer a
/// search for <c>iPhone</c> with the results for <c>iphone</c>, and no layer above could detect the
/// substitution. Two requests differing only in case are one request as far as this abstraction is
/// concerned.</para>
///
/// <para>The fold is deliberately invariant-culture, so the same input produces the same call and the
/// same key on any host locale; an implementation must not re-case or re-fold the value against the
/// ambient culture. It is a lower-casing, not full Unicode case folding — so it is not exactly the
/// <see cref="StringComparison.OrdinalIgnoreCase"/> relation, and a source that needs an equality
/// comparer of its own should use <see cref="StringComparison.Ordinal"/> on the value as given.</para>
/// </summary>
public interface IProductSource
{
    /// <summary>
    /// Whether <see cref="QueryAsync"/> applies <see cref="ProductQuery.MinPrice"/> and
    /// <see cref="ProductQuery.MaxPrice"/> itself, so the caller need not filter in memory.
    ///
    /// <para>Declared up front rather than discovered from the answer because the caller has to
    /// <em>decide how to ask</em> before asking: a cache in front of this interface must pick a key
    /// before the call, and the key is only correct if it covers exactly what the call depends on. A
    /// source that filters natively produces bound-dependent answers and must be keyed on the bounds;
    /// one that does not produces the same answer whatever they are, and keying on them would multiply
    /// full-catalog fetches for no benefit (see <c>CacheKeys.FilterCandidates</c>).</para>
    ///
    /// <para>Declaring <c>true</c> is a promise, and it is checked: the caller refuses a result whose
    /// <see cref="ProductQueryResult.PriceFilterApplied"/> contradicts it, rather than serving a page
    /// that was paginated before filtering.</para>
    /// </summary>
    bool SupportsPriceFilter { get; }

    /// <summary>Returns the page of products matching <paramref name="query"/>.</summary>
    /// <param name="query">the filter and the window of results wanted; free text already normalized</param>
    Task<ProductQueryResult> QueryAsync(ProductQuery query, CancellationToken ct = default);

    /// <summary>Returns the full detail of a single product.</summary>
    /// <exception cref="Exceptions.ProductNotFoundException">if no product has that id</exception>
    Task<Product> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>Returns the list of available category identifiers.</summary>
    Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct = default);
}
