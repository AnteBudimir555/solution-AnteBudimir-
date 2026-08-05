using Middleware.Core.Domain;
using Middleware.Infrastructure.Upstream.Dto;

namespace Middleware.Infrastructure.Upstream;

/// <summary>
/// Maps raw DummyJSON DTOs into the internal <see cref="Product"/> domain model. This is the only
/// place that knows the DummyJSON shape; everything downstream works with <see cref="Product"/>.
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
            d.Tags,
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
            d.Images,
            ToReviews(d.Reviews),
            ToDomain(d.Meta));

    private static Dimensions? ToDomain(DummyDimensions? d) =>
        d is null ? null : new Dimensions(d.Width, d.Height, d.Depth);

    private static Meta? ToDomain(DummyMeta? m) =>
        m is null ? null : new Meta(m.CreatedAt, m.UpdatedAt, m.Barcode, m.QrCode);

    private static IReadOnlyList<Review>? ToReviews(IReadOnlyList<DummyReview>? reviews) =>
        // reviewerEmail is deliberately dropped here: it is upstream PII and is never surfaced by the API.
        reviews?.Select(r => new Review(r.Rating, r.Comment, r.Date, r.ReviewerName)).ToList();
}
