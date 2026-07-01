package com.abysalto.middleware.domain;

/**
 * A single customer review for a product. The reviewer's email is intentionally not carried here:
 * it is upstream PII and must not be re-exposed through the detail endpoint.
 */
public record Review(
        Integer rating,
        String comment,
        String date,
        String reviewerName
) {
}
