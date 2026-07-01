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
        return "search|" + normalizeText(query) + "|" + page + "|" + size;
    }

    /** Key for the no-price filter path (category listing or full list): normalized category plus pagination. */
    public static String filterPage(String category, int page, int size) {
        return "page|" + normalizeText(category) + "|" + page + "|" + size;
    }

    /**
     * Key for the price-filter candidate set: normalized category plus price bounds only — deliberately
     * independent of pagination, so every page of the same filter reuses one cached upstream fetch. The
     * {@code "page|"}/{@code "cand|"} prefixes keep the two filter-cache key spaces from ever colliding.
     */
    public static String filterCandidates(String category, BigDecimal minPrice, BigDecimal maxPrice) {
        return "cand|" + normalizeText(category) + "|" + price(minPrice) + "|" + price(maxPrice);
    }

    private static String price(BigDecimal value) {
        return value == null ? "*" : value.stripTrailingZeros().toPlainString();
    }
}
