package com.abysalto.middleware.exception;

/**
 * Thrown when an upstream product source fails (network error, timeout, non-2xx response other
 * than 404). Maps to HTTP 502 Bad Gateway so clients can distinguish an upstream failure from a
 * bad request on our side.
 */
public class UpstreamException extends RuntimeException {

    public UpstreamException(String message, Throwable cause) {
        super(message, cause);
    }

    public UpstreamException(String message) {
        super(message);
    }
}
