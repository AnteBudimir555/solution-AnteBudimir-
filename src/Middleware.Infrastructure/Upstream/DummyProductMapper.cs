using System.Collections.Immutable;
using Middleware.Core.Domain;
using Middleware.Infrastructure.Upstream.Dto;

namespace Middleware.Infrastructure.Upstream;

/// <summary>
/// Maps raw DummyJSON DTOs into the internal <see cref="Product"/> domain model. This is the only
/// place that knows the DummyJSON shape; everything downstream works with <see cref="Product"/>.
///
/// <para>Every collection is passed through <see cref="Freeze{T}"/> on the way in. <see cref="Product"/>
/// is marked <c>[ImmutableObject(true)]</c>, which lets the cache share one instance across concurrent
/// requests; a <c>List&lt;T&gt;</c> reaching a domain object behind an <c>IReadOnlyList&lt;T&gt;</c>
/// would make that sharing unsafe, since the list can be cast back and mutated. Deserialized DTO
/// collections are exactly such lists, so this is where they stop.</para>
/// </summary>
internal static class DummyProductMapper
{
    public static Product ToDomain(DummyProduct d) =>
        new(
            d.Id,
            d.Title,
            d.Description,
            d.Category,
            d.Price,
            d.DiscountPercentage,
            d.Rating,
            d.Stock,
            Freeze(d.Tags),
            d.Brand,
            d.Sku,
            d.Weight,
            ToDomain(d.Dimensions),
            d.WarrantyInformation,
            d.ShippingInformation,
            d.AvailabilityStatus,
            d.ReturnPolicy,
            d.MinimumOrderQuantity,
            d.Thumbnail,
            Freeze(d.Images),
            ToReviews(d.Reviews),
            ToDomain(d.Meta));

    /// <summary>
    /// Returns a genuinely immutable copy, preserving the null/empty distinction the wire shape carries
    /// (a missing array and an empty one are different payloads, and both round-trip unchanged).
    /// </summary>
    internal static IReadOnlyList<T>? Freeze<T>(IEnumerable<T>? items) =>
        items is null ? null : ImmutableArray.CreateRange(items);

    private static Dimensions? ToDomain(DummyDimensions? d) =>
        d is null ? null : new Dimensions(d.Width, d.Height, d.Depth);

    private static Meta? ToDomain(DummyMeta? m) =>
        m is null ? null : new Meta(m.CreatedAt, m.UpdatedAt, m.Barcode, m.QrCode);

    private static IReadOnlyList<Review>? ToReviews(IReadOnlyList<DummyReview>? reviews) =>
        // reviewerEmail is deliberately dropped here: it is upstream PII and is never surfaced by the API.
        Freeze(reviews?.Select(r => new Review(r.Rating, r.Comment, r.Date, r.ReviewerName)));
}
