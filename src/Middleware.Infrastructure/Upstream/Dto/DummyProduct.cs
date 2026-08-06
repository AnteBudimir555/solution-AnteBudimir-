namespace Middleware.Infrastructure.Upstream.Dto;

/// <summary>
/// Raw DummyJSON product payload. Kept <c>internal</c> to the upstream adapter so the upstream shape
/// never leaks into the rest of the application. Unknown JSON fields are ignored by default
/// (System.Text.Json), so new upstream attributes do not break deserialization.
/// <para><see cref="Price"/> is bound directly as <see cref="decimal"/>: the JSON number is parsed
/// straight into the domain's money type without a lossy <c>double</c> hop.</para>
/// </summary>
internal sealed record DummyProduct(
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
    DummyDimensions? Dimensions,
    string? WarrantyInformation,
    string? ShippingInformation,
    string? AvailabilityStatus,
    string? ReturnPolicy,
    int? MinimumOrderQuantity,
    string? Thumbnail,
    IReadOnlyList<string>? Images,
    IReadOnlyList<DummyReview>? Reviews,
    DummyMeta? Meta);
