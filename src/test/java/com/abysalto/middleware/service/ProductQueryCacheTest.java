package com.abysalto.middleware.service;

import com.abysalto.middleware.domain.Product;
import com.abysalto.middleware.domain.ProductPage;
import com.abysalto.middleware.source.ProductSource;
import com.abysalto.middleware.support.TestData;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.extension.ExtendWith;
import org.mockito.Mock;
import org.mockito.junit.jupiter.MockitoExtension;

import java.math.BigDecimal;
import java.util.List;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

/**
 * Unit tests for {@link ProductQueryCache}. The {@code @Cacheable} annotations are inert here (no
 * Spring proxy), so these tests focus on the logic the annotations wrap: input normalization, the
 * {@code page*size} offset translation, the empty-category routing and the in-memory price filter
 * (including the null-price exclusion). Caching behaviour itself is verified in {@code CachingIT}.
 */
@ExtendWith(MockitoExtension.class)
class ProductQueryCacheTest {

    @Mock
    private ProductSource source;

    /** Cache with a generous in-memory threshold (the price-filter warn branch stays out of the way). */
    private ProductQueryCache cache() {
        return withThreshold(5000);
    }

    private ProductQueryCache withThreshold(int threshold) {
        return new ProductQueryCache(source, threshold);
    }

    @Test
    void searchNormalizesQueryAndTranslatesOffset() {
        ProductPage page = new ProductPage(List.of(), 0, 20, 10);
        when(source.searchByName("phone", 20, 10)).thenReturn(page);

        assertThat(cache().search("  Phone ", 2, 10)).isSameAs(page);
        verify(source).searchByName("phone", 20, 10);
    }

    @Test
    void categoryPageWithBlankCategoryFallsBackToFullList() {
        ProductPage page = new ProductPage(List.of(), 0, 0, 20);
        when(source.list(0, 20)).thenReturn(page);

        assertThat(cache().categoryPage("   ", 0, 20)).isSameAs(page);
        verify(source).list(0, 20);
    }

    @Test
    void categoryPageWithCategoryQueriesThatCategory() {
        ProductPage page = new ProductPage(List.of(), 0, 10, 10);
        when(source.findByCategory("beauty", 10, 10)).thenReturn(page);

        assertThat(cache().categoryPage("Beauty", 1, 10)).isSameAs(page);
        verify(source).findByCategory("beauty", 10, 10);
    }

    @Test
    void priceFilteredCandidatesKeepsOnlyPricesWithinBoundsAndDropsNullPrices() {
        List<Product> catalog = List.of(
                TestData.product(1, new BigDecimal("5")),
                TestData.product(2, new BigDecimal("10")),
                TestData.product(3, new BigDecimal("30")),
                TestData.product(4, new BigDecimal("50")),
                TestData.product(5, new BigDecimal("80")),
                TestData.product(6, null)   // must be excluded, never NPE
        );
        when(source.list(0, ProductSource.ALL)).thenReturn(new ProductPage(catalog, catalog.size(), 0, 0));

        List<Product> filtered = withThreshold(5000)
                .priceFilteredCandidates(null, new BigDecimal("10"), new BigDecimal("50"));

        assertThat(filtered).extracting(Product::id).containsExactly(2L, 3L, 4L);
    }

    @Test
    void priceFilteredCandidatesTreatsBoundsAsInclusiveAndOpenEnded() {
        List<Product> catalog = List.of(
                TestData.product(1, new BigDecimal("10")),
                TestData.product(2, new BigDecimal("100")),
                TestData.product(3, new BigDecimal("1000"))
        );
        when(source.findByCategory("beauty", 0, ProductSource.ALL))
                .thenReturn(new ProductPage(catalog, catalog.size(), 0, 0));

        // Only a lower bound -> everything >= 100 (inclusive).
        List<Product> minOnly = withThreshold(5000)
                .priceFilteredCandidates("Beauty", new BigDecimal("100"), null);

        assertThat(minOnly).extracting(Product::id).containsExactly(2L, 3L);
    }

    @Test
    void priceFilteredCandidatesStillFiltersWhenOverInMemoryThreshold() {
        List<Product> catalog = List.of(
                TestData.product(1, new BigDecimal("5")),
                TestData.product(2, new BigDecimal("50"))
        );
        when(source.list(0, ProductSource.ALL)).thenReturn(new ProductPage(catalog, catalog.size(), 0, 0));

        // threshold 1 forces the warn branch; behaviour must be unchanged.
        List<Product> filtered = withThreshold(1)
                .priceFilteredCandidates(null, null, new BigDecimal("10"));

        assertThat(filtered).extracting(Product::id).containsExactly(1L);
    }
}
