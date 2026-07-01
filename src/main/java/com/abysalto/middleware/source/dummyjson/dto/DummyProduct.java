package com.abysalto.middleware.source.dummyjson.dto;

import com.fasterxml.jackson.annotation.JsonIgnoreProperties;

import java.util.List;

/**
 * Raw DummyJSON product payload. Kept package-internal to the {@code dummyjson} source so the
 * upstream shape never leaks into the rest of the application. Unknown fields are ignored so new
 * upstream attributes do not break deserialization.
 */
@JsonIgnoreProperties(ignoreUnknown = true)
public record DummyProduct(
        long id,
        String title,
        String description,
        String category,
        Double price,
        Double discountPercentage,
        Double rating,
        Integer stock,
        List<String> tags,
        String brand,
        String sku,
        Double weight,
        DummyDimensions dimensions,
        String warrantyInformation,
        String shippingInformation,
        String availabilityStatus,
        String returnPolicy,
        Integer minimumOrderQuantity,
        String thumbnail,
        List<String> images,
        List<DummyReview> reviews,
        DummyMeta meta
) {
}
