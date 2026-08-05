namespace Middleware.Core.Domain;

/// <summary>
/// Internal, source-agnostic representation of a product. Every product source maps its upstream
/// payload into this type, so the rest of the application never depends on a concrete source shape.
/// Adding a new source means writing a new mapper, not changing any consumer.
/// </summary>
public sealed record Product(
    long Id,
    string? Title,
    string? Description,
    string? Category,
    decimal? Price,
    double? DiscountPercentage,
    double? Rating,
    int? Stock,
    IReadOnlyList<string>? Tags,
    string? Brand,
    string? Sku,
    double? Weight,
    Dimensions? Dimensions,
    string? WarrantyInformation,
    string? ShippingInformation,
    string? AvailabilityStatus,
    string? ReturnPolicy,
    int? MinimumOrderQuantity,
    string? Thumbnail,
    IReadOnlyList<string>? Images,
    IReadOnlyList<Review>? Reviews,
    Meta? Meta);
