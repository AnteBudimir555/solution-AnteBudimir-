package com.abysalto.middleware.domain;

import java.util.List;

/**
 * A page of products plus the pagination metadata reported by the source.
 *
 * @param items the products on this page
 * @param total total number of products matching the query across all pages
 * @param skip  number of products skipped before this page (offset)
 * @param limit page size requested (0 means "all", per the source contract)
 */
public record ProductPage(List<Product> items, int total, int skip, int limit) {
}
