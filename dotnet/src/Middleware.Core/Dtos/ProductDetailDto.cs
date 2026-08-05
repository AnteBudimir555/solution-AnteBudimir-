using Middleware.Core.Domain;

namespace Middleware.Core.Dtos;

/// <summary>
/// Full product shape returned by the single-product detail endpoint. Nested value objects
/// (<see cref="Dimensions"/>, <see cref="Review"/>, <see cref="Meta"/>) are shared with the domain layer.
/// </summary>
public sealed record ProductDetailDto(
    long Id,
    string? Name,
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
    string? Image,
    IReadOnlyList<string>? Images,
    IReadOnlyList<Review>? Reviews,
    Meta? Meta);
