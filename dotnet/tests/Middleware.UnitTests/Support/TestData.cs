using Middleware.Core.Domain;

namespace Middleware.UnitTests.Support;

/// <summary>
/// Shared domain fixtures for the unit tests. Keeps the many-field <see cref="Product"/> constructor
/// out of individual tests so they can focus on the fields that matter to each case.
/// </summary>
internal static class TestData
{
    /// <summary>A fully-populated product with the identifying fields under the caller's control.</summary>
    public static Product Product(long id, string? title, string? description, decimal? price, string? category) =>
        new(
            id,
            title,
            description,
            category,
            price,
            10.0,               // discountPercentage
            4.5,                // rating
            100,                // stock
            ["tag-a"],          // tags
            "AcmeBrand",        // brand
            $"SKU-{id}",        // sku
            1.5,                // weight
            new Dimensions(1.0, 2.0, 3.0),
            "1 year warranty",  // warrantyInformation
            "Ships in 3 days",  // shippingInformation
            "In Stock",         // availabilityStatus
            "30 days",          // returnPolicy
            1,                  // minimumOrderQuantity
            $"https://img/{id}.png",
            [$"https://img/{id}-1.png"],
            [new Review(5, "Great", "2024-01-01", "Alice")],
            new Meta("2024-01-01", "2024-02-01", $"barcode-{id}", $"qr-{id}"));

    /// <summary>Convenience overload for cases that only care about id/price (e.g. price-filter tests).</summary>
    public static Product Product(long id, decimal? price) =>
        Product(id, $"Product {id}", $"Description {id}", price, "misc");
}
