package com.abysalto.middleware.config;

import com.github.benmanes.caffeine.cache.Caffeine;
import org.springframework.cache.CacheManager;
import org.springframework.cache.annotation.EnableCaching;
import org.springframework.cache.caffeine.CaffeineCacheManager;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;

/**
 * Enables Spring Cache backed by Caffeine for the search and filter endpoints (TASK §4).
 *
 * <p>Both endpoints repeat identical upstream work when called with the same parameters. Caching the
 * mapped result keyed by normalized parameters (see {@link CacheKeys}) deduplicates those calls. The
 * cache manager is restricted to the two named caches below, so a typo in a {@code @Cacheable} name
 * fails fast instead of silently creating a stray cache. Bound and TTL come from
 * {@code middleware.cache.spec}.
 */
@Configuration
@EnableCaching
public class CacheConfig {

    /** Cache of name-search results, keyed by normalized query plus pagination. */
    public static final String SEARCH_CACHE = "productSearch";

    /** Cache of filter results, keyed by normalized category/price bounds plus pagination. */
    public static final String FILTER_CACHE = "productFilter";

    @Bean
    public CacheManager cacheManager(CacheProperties properties) {
        CaffeineCacheManager manager = new CaffeineCacheManager(SEARCH_CACHE, FILTER_CACHE);
        manager.setCaffeine(Caffeine.from(properties.spec()));
        return manager;
    }
}
