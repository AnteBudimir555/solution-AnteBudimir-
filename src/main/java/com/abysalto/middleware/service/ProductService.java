package com.abysalto.middleware.service;

import com.abysalto.middleware.domain.Product;
import com.abysalto.middleware.domain.ProductPage;
import com.abysalto.middleware.dto.PagedResponse;
import com.abysalto.middleware.dto.ProductDetailDto;
import com.abysalto.middleware.dto.ProductSummaryDto;
import com.abysalto.middleware.source.ProductSource;
import org.springframework.stereotype.Service;

import java.math.BigDecimal;
import java.util.List;

/**
 * Application service over the {@link ProductSource} abstraction. Responsible for pagination,
 * mapping to API DTOs and the in-service price-range filtering that the upstream cannot do.
 *
 * <p>Cache-backed upstream access for search/filter is delegated to {@link ProductQueryCache} (a
 * separate bean so Spring's cache proxy is honoured and the price-filter candidate set can be cached
 * independently of pagination); this service handles orchestration, in-service paging and mapping.
 */
@Service
public class ProductService {

    private final ProductSource source;
    private final ProductQueryCache queries;
    private final ProductMapper mapper;

    public ProductService(ProductSource source, ProductQueryCache queries, ProductMapper mapper) {
        this.source = source;
        this.queries = queries;
        this.mapper = mapper;
    }

    /** Paginated trimmed list of all products. */
    public PagedResponse<ProductSummaryDto> list(int page, int size) {
        ProductPage result = source.list(offset(page, size), size);
        return toSummaryPage(result.items(), page, size, result.total());
    }

    /** Full detail of a single product. */
    public ProductDetailDto getById(long id) {
        return mapper.toDetail(source.getById(id));
    }

    /**
     * Filters by category and/or price range (combinable). Category is pushed down to the source;
     * price range is applied in-service.
     *
     * <p>When no price bound is present the source paginates directly (cached per page). When a price
     * bound is present the full price-filtered candidate set is fetched once (cached by category and
     * price bounds via {@link ProductQueryCache}, independent of pagination) and the requested page is
     * sliced from it here, so paging through the result never re-fetches the upstream catalog.
     */
    public PagedResponse<ProductSummaryDto> filter(String category, BigDecimal minPrice, BigDecimal maxPrice,
                                                   int page, int size) {
        boolean hasPriceFilter = minPrice != null || maxPrice != null;

        if (!hasPriceFilter) {
            ProductPage result = queries.categoryPage(category, page, size);
            return toSummaryPage(result.items(), page, size, result.total());
        }

        List<Product> filtered = queries.priceFilteredCandidates(category, minPrice, maxPrice);
        List<Product> pageItems = paginate(filtered, page, size);
        return toSummaryPage(pageItems, page, size, filtered.size());
    }

    /** Free-text search by product name (pushed down to the source, cached per page). */
    public PagedResponse<ProductSummaryDto> searchByName(String query, int page, int size) {
        ProductPage result = queries.search(query, page, size);
        return toSummaryPage(result.items(), page, size, result.total());
    }

    /** Available category identifiers. */
    public List<String> categories() {
        return source.categories();
    }

    // --- helpers -----------------------------------------------------------

    private static int offset(int page, int size) {
        return page * size;
    }

    private static <T> List<T> paginate(List<T> items, int page, int size) {
        int from = Math.min(offset(page, size), items.size());
        int to = Math.min(from + size, items.size());
        return items.subList(from, to);
    }

    private PagedResponse<ProductSummaryDto> toSummaryPage(List<Product> products, int page, int size, long total) {
        List<ProductSummaryDto> summaries = products.stream().map(mapper::toSummary).toList();
        return PagedResponse.of(summaries, page, size, total);
    }
}
