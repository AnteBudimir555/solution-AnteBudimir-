package com.abysalto.middleware.domain;

import java.math.BigDecimal;
import java.util.List;

/**
 * Internal, source-agnostic representation of a product.
 * <p>
 * Every {@link com.abysalto.middleware.source.ProductSource} maps its upstream payload into this
 * type, so the rest of the application never depends on a concrete source (e.g. DummyJSON) shape.
 * Adding a new source means writing a new mapper, not changing any consumer.
 */
public record Product(
        long id,
        String title,
        String description,
        String category,
        BigDecimal price,
        Double discountPercentage,
        Double rating,
        Integer stock,
        List<String> tags,
        String brand,
        String sku,
        Double weight,
        Dimensions dimensions,
        String warrantyInformation,
        String shippingInformation,
        String availabilityStatus,
        String returnPolicy,
        Integer minimumOrderQuantity,
        String thumbnail,
        List<String> images,
        List<Review> reviews,
        Meta meta
) {
}
