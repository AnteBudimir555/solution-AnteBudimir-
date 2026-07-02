package com.abysalto.middleware.service;

import com.abysalto.middleware.domain.Product;
import com.abysalto.middleware.domain.ProductPage;
import com.abysalto.middleware.dto.PagedResponse;
import com.abysalto.middleware.dto.ProductDetailDto;
import com.abysalto.middleware.dto.ProductSummaryDto;
import com.abysalto.middleware.source.ProductSource;
import com.abysalto.middleware.support.TestData;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.extension.ExtendWith;
import org.mockito.Mock;
import org.mockito.junit.jupiter.MockitoExtension;

import java.math.BigDecimal;
import java.util.List;
import java.util.stream.IntStream;

import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.verifyNoInteractions;
import static org.mockito.Mockito.when;

/**
 * Unit tests for {@link ProductService} orchestration: offset computation, DTO mapping, the
 * no-price vs price-filter routing and the in-service pagination slice (including out-of-range
 * pages). A real {@link ProductMapper} is used so mapping is exercised end-to-end; the cache and
 * source boundaries are mocked.
 */
@ExtendWith(MockitoExtension.class)
class ProductServiceTest {

    @Mock
    private ProductSource source;

    @Mock
    private ProductQueryCache queries;

    private final ProductMapper mapper = new ProductMapper(100);

    private ProductService service() {
        return new ProductService(source, queries, mapper);
    }

    @Test
    void listMapsPageToSummariesWithPaginationMetadata() {
        List<Product> items = List.of(
                TestData.product(1, new BigDecimal("10")),
                TestData.product(2, new BigDecimal("20")));
        when(source.list(0, 20)).thenReturn(new ProductPage(items, 42, 0, 20));

        PagedResponse<ProductSummaryDto> response = service().list(0, 20);

        assertThat(response.items()).hasSize(2);
        assertThat(response.items().get(0).name()).isEqualTo("Product 1");
        assertThat(response.page()).isZero();
        assertThat(response.size()).isEqualTo(20);
        assertThat(response.totalItems()).isEqualTo(42);
        assertThat(response.totalPages()).isEqualTo(3);
    }

    @Test
    void listTranslatesPageIndexToOffset() {
        when(source.list(40, 20)).thenReturn(new ProductPage(List.of(), 100, 40, 20));
        service().list(2, 20);
        verify(source).list(40, 20);
    }

    @Test
    void getByIdReturnsMappedDetail() {
        when(source.getById(7)).thenReturn(TestData.product(7, "Camera", "desc", new BigDecimal("99"), "photo"));

        ProductDetailDto detail = service().getById(7);

        assertThat(detail.id()).isEqualTo(7);
        assertThat(detail.name()).isEqualTo("Camera");
    }

    @Test
    void filterWithoutPriceBoundsDelegatesToCategoryPage() {
        List<Product> items = List.of(TestData.product(1, new BigDecimal("10")));
        when(queries.categoryPage("beauty", 0, 20)).thenReturn(new ProductPage(items, 1, 0, 20));

        PagedResponse<ProductSummaryDto> response = service().filter("beauty", null, null, 0, 20);

        assertThat(response.items()).hasSize(1);
        assertThat(response.totalItems()).isEqualTo(1);
        verify(queries).categoryPage("beauty", 0, 20);
        // The price-filter candidate path must not be touched when no bound is present.
        verifyNoInteractions(source);
    }

    @Test
    void filterWithPriceBoundsSlicesRequestedPageFromCandidateSet() {
        List<Product> candidates = IntStream.rangeClosed(1, 5)
                .mapToObj(i -> TestData.product(i, new BigDecimal(i * 10)))
                .toList();
        when(queries.priceFilteredCandidates("beauty", new BigDecimal("10"), new BigDecimal("50")))
                .thenReturn(candidates);

        PagedResponse<ProductSummaryDto> page0 =
                service().filter("beauty", new BigDecimal("10"), new BigDecimal("50"), 0, 2);

        assertThat(page0.items()).hasSize(2);
        assertThat(page0.totalItems()).isEqualTo(5);
        assertThat(page0.totalPages()).isEqualTo(3);
    }

    @Test
    void filterWithPriceBoundsClampsPageBeyondTheEndToEmpty() {
        List<Product> candidates = List.of(
                TestData.product(1, new BigDecimal("10")),
                TestData.product(2, new BigDecimal("20")));
        when(queries.priceFilteredCandidates(null, new BigDecimal("1"), null)).thenReturn(candidates);

        // page 5 of size 2 is well past the 2-item candidate set: no items, but total is preserved.
        PagedResponse<ProductSummaryDto> response = service().filter(null, new BigDecimal("1"), null, 5, 2);

        assertThat(response.items()).isEmpty();
        assertThat(response.totalItems()).isEqualTo(2);
    }

    @Test
    void searchDelegatesToCache() {
        when(queries.search("phone", 0, 20)).thenReturn(new ProductPage(List.of(), 0, 0, 20));
        service().searchByName("phone", 0, 20);
        verify(queries).search("phone", 0, 20);
    }

    @Test
    void categoriesDelegatesToSource() {
        when(source.categories()).thenReturn(List.of("beauty", "laptops"));
        assertThat(service().categories()).containsExactly("beauty", "laptops");
    }
}
