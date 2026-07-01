package com.abysalto.middleware.dto;

import io.swagger.v3.oas.annotations.media.Schema;

import java.math.BigDecimal;

/**
 * Trimmed product shape used by the list/filter/search endpoints (TASK §3.1): just the image,
 * name, price and a short description capped at 100 characters.
 */
@Schema(description = "Trimmed product summary for list/filter/search results")
public record ProductSummaryDto(
        @Schema(description = "Product thumbnail image URL") String image,
        @Schema(description = "Product name") String name,
        @Schema(description = "Product price") BigDecimal price,
        @Schema(description = "Description truncated to at most 100 characters") String shortDescription
) {
}
