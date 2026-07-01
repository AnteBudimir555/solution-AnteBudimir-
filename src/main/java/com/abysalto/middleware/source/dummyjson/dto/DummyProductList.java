package com.abysalto.middleware.source.dummyjson.dto;

import com.fasterxml.jackson.annotation.JsonIgnoreProperties;

import java.util.List;

/** DummyJSON paginated list envelope ({@code products}, {@code total}, {@code skip}, {@code limit}). */
@JsonIgnoreProperties(ignoreUnknown = true)
public record DummyProductList(
        List<DummyProduct> products,
        int total,
        int skip,
        int limit
) {
}
