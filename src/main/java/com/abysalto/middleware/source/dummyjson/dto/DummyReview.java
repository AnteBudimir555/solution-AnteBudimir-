package com.abysalto.middleware.source.dummyjson.dto;

import com.fasterxml.jackson.annotation.JsonIgnoreProperties;

@JsonIgnoreProperties(ignoreUnknown = true)
public record DummyReview(
        Integer rating,
        String comment,
        String date,
        String reviewerName,
        String reviewerEmail
) {
}
