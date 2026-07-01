package com.abysalto.middleware.config;

import java.math.BigDecimal;
import java.util.Locale;

/**
 * Builds normalized cache keys for the search/filter caches so parameter sets that are semantically
 * identical map to the same entry: case/whitespace differences in text, equivalent numeric scales
 * (e.g. {@code 10} vs {@code 10.00}) and absent price bounds all collapse to one key.
 *
 * <p>{@link #normalizeText(String)} is the single source of truth for free-text normalization: the
 * {@code @Cacheable} SpEL keys in {@code ProductService} and the service body both run the query and
 * category through it before use, so the cache key and the actual upstream call can never diverge
 * (two inputs share a key only when they also produce the same upstream call).
 */
public final class CacheKeys {

    private CacheKeys() {
    }

    /** Normalizes a free-text param (query/category): trimmed and lower-cased; null/blank become "". */
    public static String normalizeText(String value) {
        return value == null ? "" : value.strip().toLowerCase(Locale.ROOT);
    }

    /** Key for name search: normalized query plus pagination. */
    public static String search(String query, int page, int size) {
        return normalizeText(query) + "|" + page + "|" + size;
    }

    /** Key for filtering: normalized category and price bounds plus pagination. */
    public static String filter(String category, BigDecimal minPrice, BigDecimal maxPrice, int page, int size) {
        return normalizeText(category) + "|" + price(minPrice) + "|" + price(maxPrice) + "|" + page + "|" + size;
    }

    private static String price(BigDecimal value) {
        return value == null ? "*" : value.stripTrailingZeros().toPlainString();
    }
}
