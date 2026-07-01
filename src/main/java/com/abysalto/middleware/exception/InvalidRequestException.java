package com.abysalto.middleware.exception;

/**
 * Thrown for a semantically invalid request that Bean Validation annotations cannot express on their
 * own (e.g. {@code minPrice > maxPrice}). Rendered as a 400 by {@code GlobalExceptionHandler}.
 *
 * <p>Using a dedicated type — rather than a raw {@link IllegalArgumentException} — keeps the 400
 * mapping intentional: an unrelated {@code IllegalArgumentException} thrown deeper in the stack is a
 * server-side fault and correctly surfaces as a 500 instead of being mislabelled a bad request.
 */
public class InvalidRequestException extends RuntimeException {

    public InvalidRequestException(String message) {
        super(message);
    }
}
