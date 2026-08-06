namespace Middleware.Core.Abstractions;

/// <summary>
/// What a caller wants from an <see cref="IProductSource"/>: which products, and which window of
/// them. One query object replaces the separate list / by-category / by-name signatures, so that a
/// source can be <em>asked</em> for something it might be able to do natively instead of being handed
/// the primitives the middleware happens to need.
///
/// <para>The shape exists for one reason: a filter a source cannot express is a filter the middleware
/// must apply in process, and applying it in process means materializing everything that might match.
/// A source that can filter on price in its own store (a SQL <c>WHERE</c>, an Elasticsearch range)
/// had no way to say so under the old signatures and was handed the entire catalog regardless. What
/// it applied comes back on <see cref="ProductQueryResult"/>.</para>
///
/// <para>Free text arrives already normalized — see the free-text convention on
/// <see cref="IProductSource"/>. All members are optional: the empty query is "the whole catalog".</para>
/// </summary>
public sealed record ProductQuery
{
    /// <summary>Category to restrict to; <c>null</c> for the whole catalog. Already normalized.</summary>
    public string? Category { get; init; }

    /// <summary>Free text the product name must match; <c>null</c> for no name filter. Already normalized.</summary>
    public string? NameContains { get; init; }

    /// <summary>Inclusive lower price bound; <c>null</c> for unbounded below.</summary>
    public decimal? MinPrice { get; init; }

    /// <summary>Inclusive upper price bound; <c>null</c> for unbounded above.</summary>
    public decimal? MaxPrice { get; init; }

    /// <summary>Zero-based offset of the first item to return.</summary>
    public int Skip { get; init; }

    /// <summary>
    /// How many items to return; <c>null</c> asks for <em>every</em> match.
    ///
    /// <para>This replaces the old <c>IProductSource.All = 0</c> sentinel, which was DummyJSON's own
    /// wire convention (<c>limit=0</c> means everything) promoted into the abstraction and passed
    /// straight through to the URL. Two things were wrong with it. Every future source inherited a
    /// convention it had no reason to share; and because <c>0</c> was a legitimate-looking number
    /// rather than a distinguished value, a caller that computed a page size of zero <em>silently
    /// downloaded the entire catalog</em> instead of returning nothing. As <c>null</c> the request for
    /// everything has to be written deliberately, and a zero or negative limit is now an error a
    /// source is expected to reject rather than a fetch of unbounded size.</para>
    ///
    /// <para>Asking for everything is still a request a large source cannot honour. The abstraction
    /// cannot fix that on its own — what fixes it is the source being able to apply the filter, so the
    /// middleware stops needing everything in the first place.</para>
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>Whether either price bound is set — i.e. whether the price filter has to happen at all.</summary>
    public bool HasPriceFilter => MinPrice is not null || MaxPrice is not null;
}
