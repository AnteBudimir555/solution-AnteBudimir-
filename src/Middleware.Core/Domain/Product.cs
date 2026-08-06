using System.ComponentModel;

namespace Middleware.Core.Domain;

/// <summary>
/// Internal, source-agnostic representation of a product. Every product source maps its upstream
/// payload into this type, so the rest of the application never depends on a concrete source shape.
/// Adding a new source means writing a new mapper, not changing any consumer.
///
/// <para><see cref="ImmutableObjectAttribute"/> is how <c>HybridCache</c> is told it may hand the same
/// instance to every caller instead of re-deserializing the entry on each hit — which it otherwise
/// does, because it cannot infer immutability from a record. Measured: without the attribute two hits
/// on one key return two different instances.</para>
///
/// <para><b>This makes the promise real, so it has to be true.</b> The collection members are typed as
/// <see cref="IReadOnlyList{T}"/>, which is a read-only <em>view</em>, not an immutable collection — a
/// <c>List&lt;T&gt;</c> behind one can be cast back and mutated, and with a shared instance that
/// mutation would be visible to every concurrent request holding it. Sources must therefore populate
/// these with a genuinely immutable collection; <c>DummyProductMapper</c> does so via its
/// <c>Freeze</c> helper.</para>
/// </summary>
[ImmutableObject(true)]
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
