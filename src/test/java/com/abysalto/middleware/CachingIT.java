package com.abysalto.middleware;

import com.abysalto.middleware.domain.ProductPage;
import com.abysalto.middleware.service.ProductQueryCache;
import com.abysalto.middleware.source.ProductSource;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.cache.CacheManager;
import org.springframework.test.context.ActiveProfiles;
import org.springframework.test.context.bean.override.mockito.MockitoBean;

import java.math.BigDecimal;
import java.util.List;

import static org.mockito.Mockito.times;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

/**
 * Integration test proving the {@code @Cacheable} boundary actually deduplicates upstream work:
 * with the real Spring cache proxy in place, repeated equivalent queries must hit the source only
 * once, and inputs that normalize to the same key (case/whitespace) must share that single call.
 */
@SpringBootTest
@ActiveProfiles("test")
class CachingIT {

    @Autowired
    private ProductQueryCache queries;

    @Autowired
    private CacheManager cacheManager;

    @MockitoBean
    private ProductSource source;

    @BeforeEach
    void clearCaches() {
        cacheManager.getCacheNames().forEach(name -> cacheManager.getCache(name).clear());
    }

    @Test
    void repeatedSearchHitsUpstreamOnce() {
        when(source.searchByName("phone", 0, 20)).thenReturn(new ProductPage(List.of(), 0, 0, 20));

        queries.search("phone", 0, 20);
        queries.search("phone", 0, 20);

        verify(source, times(1)).searchByName("phone", 0, 20);
    }

    @Test
    void searchInputsThatNormalizeToTheSameKeyShareOneUpstreamCall() {
        when(source.searchByName("phone", 0, 20)).thenReturn(new ProductPage(List.of(), 0, 0, 20));

        queries.search("Phone", 0, 20);
        queries.search("  phone  ", 0, 20);

        verify(source, times(1)).searchByName("phone", 0, 20);
    }

    @Test
    void priceFilterCandidateSetIsFetchedOncePerCategoryAndBounds() {
        when(source.list(0, ProductSource.ALL)).thenReturn(new ProductPage(List.of(), 0, 0, 0));

        queries.priceFilteredCandidates(null, new BigDecimal("10"), new BigDecimal("50"));
        // Equivalent numeric scale must reuse the cached candidate set (no second upstream fetch).
        queries.priceFilteredCandidates(null, new BigDecimal("10.00"), new BigDecimal("50.0"));

        verify(source, times(1)).list(0, ProductSource.ALL);
    }
}
