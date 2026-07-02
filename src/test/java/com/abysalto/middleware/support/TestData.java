package com.abysalto.middleware.support;

import com.abysalto.middleware.domain.Dimensions;
import com.abysalto.middleware.domain.Meta;
import com.abysalto.middleware.domain.Product;
import com.abysalto.middleware.domain.Review;

import java.math.BigDecimal;
import java.util.List;

/**
 * Shared domain fixtures for the unit/integration tests. Keeps the many-field {@link Product}
 * constructor out of individual tests so they can focus on the fields that matter to each case.
 */
public final class TestData {

    private TestData() {
    }

    /** A fully-populated product with the identifying fields under the caller's control. */
    public static Product product(long id, String title, String description, BigDecimal price, String category) {
        return new Product(
                id,
                title,
                description,
                category,
                price,
                10.0,               // discountPercentage
                4.5,                // rating
                100,                // stock
                List.of("tag-a"),   // tags
                "AcmeBrand",        // brand
                "SKU-" + id,        // sku
                1.5,                // weight
                new Dimensions(1.0, 2.0, 3.0),
                "1 year warranty",  // warrantyInformation
                "Ships in 3 days",  // shippingInformation
                "In Stock",         // availabilityStatus
                "30 days",          // returnPolicy
                1,                  // minimumOrderQuantity
                "https://img/" + id + ".png",
                List.of("https://img/" + id + "-1.png"),
                List.of(new Review(5, "Great", "2024-01-01", "Alice")),
                new Meta("2024-01-01", "2024-02-01", "barcode-" + id, "qr-" + id)
        );
    }

    /** Convenience overload for cases that only care about id/price (e.g. price-filter tests). */
    public static Product product(long id, BigDecimal price) {
        return product(id, "Product " + id, "Description " + id, price, "misc");
    }
}
