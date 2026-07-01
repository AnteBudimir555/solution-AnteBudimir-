package com.abysalto.middleware.dto;

import com.abysalto.middleware.domain.Dimensions;
import com.abysalto.middleware.domain.Meta;
import com.abysalto.middleware.domain.Review;
import io.swagger.v3.oas.annotations.media.Schema;

import java.math.BigDecimal;
import java.util.List;

/**
 * Full product shape returned by the single-product detail endpoint (TASK §3.2). Nested value
 * objects ({@link Dimensions}, {@link Review}, {@link Meta}) are simple immutable records shared
 * with the domain layer.
 */
@Schema(description = "Full product detail")
public record ProductDetailDto(
        long id,
        String name,
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
        String image,
        List<String> images,
        List<Review> reviews,
        Meta meta
) {
}
