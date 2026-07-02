package com.abysalto.middleware.dto;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;

import java.util.List;

import static org.assertj.core.api.Assertions.assertThat;

/**
 * Unit tests for the {@link PagedResponse#of} factory, specifically the {@code totalPages}
 * derivation including the exact-multiple and division-guard edge cases.
 */
class PagedResponseTest {

    @ParameterizedTest
    @CsvSource({
            "45, 20, 3",   // partial last page rounds up
            "40, 20, 2",   // exact multiple
            "0,  20, 0",   // no items -> no pages
            "1,  20, 1"    // single item still one page
    })
    void computesTotalPages(long totalItems, int size, int expectedPages) {
        PagedResponse<String> response = PagedResponse.of(List.of(), 0, size, totalItems);
        assertThat(response.totalPages()).isEqualTo(expectedPages);
    }

    @Test
    void guardsAgainstNonPositivePageSize() {
        // size <= 0 would divide by zero; the factory must clamp totalPages to 0 instead.
        assertThat(PagedResponse.of(List.of(), 0, 0, 10).totalPages()).isZero();
    }

    @Test
    void carriesItemsAndPaginationMetadata() {
        List<String> items = List.of("a", "b");
        PagedResponse<String> response = PagedResponse.of(items, 2, 20, 100);
        assertThat(response.items()).isEqualTo(items);
        assertThat(response.page()).isEqualTo(2);
        assertThat(response.size()).isEqualTo(20);
        assertThat(response.totalItems()).isEqualTo(100);
        assertThat(response.totalPages()).isEqualTo(5);
    }
}
