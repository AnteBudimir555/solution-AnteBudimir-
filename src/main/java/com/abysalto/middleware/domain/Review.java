package com.abysalto.middleware.domain;

/** A single customer review for a product. */
public record Review(
        Integer rating,
        String comment,
        String date,
        String reviewerName,
        String reviewerEmail
) {
}
