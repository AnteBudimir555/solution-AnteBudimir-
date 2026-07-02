package com.abysalto.middleware.service;

import com.abysalto.middleware.domain.Product;
import com.abysalto.middleware.dto.ProductDetailDto;
import com.abysalto.middleware.dto.ProductSummaryDto;
import com.abysalto.middleware.support.TestData;
import org.junit.jupiter.api.Test;

import java.math.BigDecimal;

import static org.assertj.core.api.Assertions.assertThat;

/**
 * Unit tests for {@link ProductMapper}: the summary shape carries only the trimmed fields (with the
 * description truncated to the configured cap), while the detail shape is a faithful full copy.
 */
class ProductMapperTest {

    private final ProductMapper mapper = new ProductMapper(100);

    @Test
    void toSummaryExposesOnlyTrimmedFields() {
        Product product = TestData.product(1, "Phone", "A short description", new BigDecimal("9.99"), "smartphones");

        ProductSummaryDto summary = mapper.toSummary(product);

        assertThat(summary.image()).isEqualTo(product.thumbnail());
        assertThat(summary.name()).isEqualTo("Phone");
        assertThat(summary.price()).isEqualByComparingTo("9.99");
        assertThat(summary.shortDescription()).isEqualTo("A short description");
    }

    @Test
    void toSummaryTruncatesLongDescription() {
        String longDescription = "word ".repeat(60).trim(); // ~300 chars
        Product product = TestData.product(1, "Phone", longDescription, new BigDecimal("9.99"), "smartphones");

        ProductSummaryDto summary = mapper.toSummary(product);

        assertThat(summary.shortDescription())
                .hasSizeLessThanOrEqualTo(100)
                .endsWith("…");
    }

    @Test
    void toSummaryKeepsNullDescriptionNull() {
        Product product = TestData.product(1, "Phone", null, new BigDecimal("9.99"), "smartphones");
        assertThat(mapper.toSummary(product).shortDescription()).isNull();
    }

    @Test
    void toDetailCopiesAllFields() {
        Product product = TestData.product(42, "Laptop", "Full detail description", new BigDecimal("1200.00"), "laptops");

        ProductDetailDto detail = mapper.toDetail(product);

        assertThat(detail.id()).isEqualTo(42);
        assertThat(detail.name()).isEqualTo("Laptop");
        assertThat(detail.image()).isEqualTo(product.thumbnail());
        assertThat(detail.description()).isEqualTo("Full detail description");
        assertThat(detail.category()).isEqualTo("laptops");
        assertThat(detail.price()).isEqualByComparingTo("1200.00");
        assertThat(detail.tags()).isEqualTo(product.tags());
        assertThat(detail.reviews()).isEqualTo(product.reviews());
        assertThat(detail.dimensions()).isEqualTo(product.dimensions());
        assertThat(detail.meta()).isEqualTo(product.meta());
    }
}
