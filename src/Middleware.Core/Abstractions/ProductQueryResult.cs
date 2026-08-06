using Middleware.Core.Domain;

namespace Middleware.Core.Abstractions;

/// <summary>
/// The answer to a <see cref="ProductQuery"/>: the page itself, plus what the source actually applied.
///
/// <para><see cref="PriceFilterApplied"/> is the honest half. A source is free to ignore the price
/// bounds — DummyJSON has no way to express them — and the caller then has to apply them itself over
/// what came back. Reporting it per call rather than assuming it means the caller never has to guess,
/// and a source that can push the filter down for some queries but not others can say so.</para>
///
/// <para>It does <em>not</em> replace <see cref="IProductSource.SupportsPriceFilter"/>, and the
/// distinction matters: a cache has to choose a key <em>before</em> it makes the call, so it needs the
/// declared capability up front. This flag is what confirms afterwards that the declaration held —
/// see <c>ProductQueryCache</c>, which refuses the answer when it did not.</para>
/// </summary>
/// <param name="Page">The products and the pagination metadata the source reported.</param>
/// <param name="PriceFilterApplied">
/// Whether <see cref="ProductQuery.MinPrice"/>/<see cref="ProductQuery.MaxPrice"/> were applied by the
/// source. When false the caller must apply them itself — and, because the source paginated an
/// unfiltered set, cannot trust <see cref="Domain.ProductPage.Total"/> as a filtered count.
/// </param>
public sealed record ProductQueryResult(ProductPage Page, bool PriceFilterApplied);
