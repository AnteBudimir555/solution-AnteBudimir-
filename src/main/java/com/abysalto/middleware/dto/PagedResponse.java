package com.abysalto.middleware.dto;

import io.swagger.v3.oas.annotations.media.Schema;

import java.util.List;

/**
 * Generic pagination envelope returned by the list/filter/search endpoints.
 *
 * @param items      the items on the current page
 * @param page       zero-based page index
 * @param size       requested page size
 * @param totalItems total number of matching items across all pages
 * @param totalPages total number of pages for the given size
 */
@Schema(description = "Paginated response envelope")
public record PagedResponse<T>(
        List<T> items,
        int page,
        int size,
        long totalItems,
        int totalPages
) {

    public static <T> PagedResponse<T> of(List<T> items, int page, int size, long totalItems) {
        int totalPages = size <= 0 ? 0 : (int) Math.ceil((double) totalItems / size);
        return new PagedResponse<>(items, page, size, totalItems, totalPages);
    }
}
