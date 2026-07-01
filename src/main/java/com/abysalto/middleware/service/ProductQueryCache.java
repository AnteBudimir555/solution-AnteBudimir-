package com.abysalto.middleware.service;

import com.abysalto.middleware.config.CacheConfig;
import com.abysalto.middleware.config.CacheKeys;
import com.abysalto.middleware.domain.Product;
import com.abysalto.middleware.domain.ProductPage;
import com.abysalto.middleware.source.ProductSource;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.cache.annotation.Cacheable;
import org.springframework.stereotype.Service;

import java.math.BigDecimal;
import java.util.List;

/**
 * Cache-aware access to the {@link ProductSource} for the search and filter endpoints (TASK §4).
 *
 * <p>The {@code @Cacheable} methods live in their own bean, separate from {@link ProductService}, for
 * two reasons:
 * <ul>
 *   <li>Spring's cache proxy only intercepts calls made <em>through</em> the bean. If these methods sat
 *       on {@code ProductService} and were invoked from another {@code ProductService} method, that
 *       self-invocation would bypass the proxy and silently skip the cache.</li>
 *   <li>The price-filter candidate set is cached by category + price bounds only —
 *       {@link #priceFilteredCandidates} is deliberately independent of pagination — so paging through a
 *       filtered result reuses a single upstream fetch instead of re-fetching the full catalog per page.</li>
 * </ul>
 *
 * <p>Each method normalizes its free-text inputs via {@link CacheKeys}, the same normalization the
 * {@code @Cacheable} SpEL keys apply, so the cache key and the actual upstream call can never diverge.
 */
@Service
public class ProductQueryCache {

    private static final Logger log = LoggerFactory.getLogger(ProductQueryCache.class);

    private final ProductSource source;

    public ProductQueryCache(ProductSource source) {
        this.source = source;
    }

    /** One page of name-search results (pagination pushed down to the source). */
    @Cacheable(cacheNames = CacheConfig.SEARCH_CACHE, sync = true,
            key = "T(com.abysalto.middleware.config.CacheKeys).search(#query, #page, #size)")
    public ProductPage search(String query, int page, int size) {
        String normalizedQuery = CacheKeys.normalizeText(query);
        return source.searchByName(normalizedQuery, offset(page, size), size);
    }

    /** One page of the no-price path: category listing (or the full list), paginated by the source. */
    @Cacheable(cacheNames = CacheConfig.FILTER_CACHE, sync = true,
            key = "T(com.abysalto.middleware.config.CacheKeys).filterPage(#category, #page, #size)")
    public ProductPage categoryPage(String category, int page, int size) {
        String normalizedCategory = CacheKeys.normalizeText(category);
        return normalizedCategory.isEmpty()
                ? source.list(offset(page, size), size)
                : source.findByCategory(normalizedCategory, offset(page, size), size);
    }

    /**
     * The full candidate set for a price-filtered query (optionally scoped to a category), already
     * price-filtered. Cached by category + price bounds only — not by pagination — so the service can
     * slice any page out of it without another upstream call.
     */
    @Cacheable(cacheNames = CacheConfig.FILTER_CACHE, sync = true,
            key = "T(com.abysalto.middleware.config.CacheKeys).filterCandidates(#category, #minPrice, #maxPrice)")
    public List<Product> priceFilteredCandidates(String category, BigDecimal minPrice, BigDecimal maxPrice) {
        String normalizedCategory = CacheKeys.normalizeText(category);
        ProductPage candidates = normalizedCategory.isEmpty()
                ? source.list(0, ProductSource.ALL)
                : source.findByCategory(normalizedCategory, 0, ProductSource.ALL);

        List<Product> filtered = candidates.items().stream()
                .filter(p -> matchesPrice(p.price(), minPrice, maxPrice))
                .toList();
        log.debug("Price filter [{}, {}] on {} candidates -> {} matches",
                minPrice, maxPrice, candidates.items().size(), filtered.size());
        return filtered;
    }

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
}
