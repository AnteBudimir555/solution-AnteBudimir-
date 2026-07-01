package com.abysalto.middleware.service;

import com.abysalto.middleware.domain.Product;
import com.abysalto.middleware.domain.ProductPage;
import com.abysalto.middleware.dto.PagedResponse;
import com.abysalto.middleware.dto.ProductDetailDto;
import com.abysalto.middleware.dto.ProductSummaryDto;
import com.abysalto.middleware.source.ProductSource;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.stereotype.Service;
import org.springframework.util.StringUtils;

import java.math.BigDecimal;
import java.util.List;

/**
 * Application service over the {@link ProductSource} abstraction. Responsible for pagination,
 * mapping to API DTOs and the in-service price-range filtering that the upstream cannot do.
 */
@Service
public class ProductService {

    private static final Logger log = LoggerFactory.getLogger(ProductService.class);

    /** Source limit value meaning "return all matching items" (see {@link ProductSource}). */
    private static final int ALL = 0;

    private final ProductSource source;
    private final ProductMapper mapper;

    public ProductService(ProductSource source, ProductMapper mapper) {
        this.source = source;
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
     * price range is applied in-service. When a price bound is present the full candidate set is
     * fetched so filtering and pagination stay consistent across pages.
     */
    public PagedResponse<ProductSummaryDto> filter(String category, BigDecimal minPrice, BigDecimal maxPrice,
                                                   int page, int size) {
        boolean hasCategory = StringUtils.hasText(category);
        boolean hasPriceFilter = minPrice != null || maxPrice != null;

        if (!hasPriceFilter) {
            // No in-service filtering needed: let the source paginate.
            ProductPage result = hasCategory
                    ? source.findByCategory(category, offset(page, size), size)
                    : source.list(offset(page, size), size);
            return toSummaryPage(result.items(), page, size, result.total());
        }

        // Price filter present: fetch the full candidate set, filter, then paginate in-service.
        ProductPage candidates = hasCategory
                ? source.findByCategory(category, 0, ALL)
                : source.list(0, ALL);

        List<Product> filtered = candidates.items().stream()
                .filter(p -> matchesPrice(p.price(), minPrice, maxPrice))
                .toList();
        log.debug("Price filter [{}, {}] on {} candidates -> {} matches",
                minPrice, maxPrice, candidates.items().size(), filtered.size());

        List<Product> pageItems = paginate(filtered, page, size);
        return toSummaryPage(pageItems, page, size, filtered.size());
    }

    /** Free-text search by product name (pushed down to the source). */
    public PagedResponse<ProductSummaryDto> searchByName(String query, int page, int size) {
        ProductPage result = source.searchByName(query, offset(page, size), size);
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

    private static boolean matchesPrice(BigDecimal price, BigDecimal min, BigDecimal max) {
        if (price == null) {
            return false;
        }
        if (min != null && price.compareTo(min) < 0) {
            return false;
        }
        return max == null || price.compareTo(max) <= 0;
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
