package com.abysalto.middleware.exception;

/** Thrown when a requested product does not exist in the source. Maps to HTTP 404. */
public class ProductNotFoundException extends RuntimeException {

    public ProductNotFoundException(long id) {
        super("Product not found: " + id);
    }
}
