package com.abysalto.middleware.exception;

import jakarta.validation.ConstraintViolationException;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.context.MessageSourceResolvable;
import org.springframework.http.HttpStatus;
import org.springframework.http.ProblemDetail;
import org.springframework.security.access.AccessDeniedException;
import org.springframework.security.core.AuthenticationException;
import org.springframework.web.bind.MethodArgumentNotValidException;
import org.springframework.web.bind.MissingServletRequestParameterException;
import org.springframework.web.bind.annotation.ExceptionHandler;
import org.springframework.web.bind.annotation.RestControllerAdvice;
import org.springframework.web.method.annotation.HandlerMethodValidationException;
import org.springframework.web.method.annotation.MethodArgumentTypeMismatchException;

import java.net.URI;
import java.time.Instant;
import java.util.stream.Collectors;

/**
 * Centralized error handling. Every failure is rendered as an RFC-7807 {@link ProblemDetail} with
 * a sensible HTTP status, so clients get a consistent error body across the API.
 */
@RestControllerAdvice
public class GlobalExceptionHandler {

    private static final Logger log = LoggerFactory.getLogger(GlobalExceptionHandler.class);

    @ExceptionHandler(ProductNotFoundException.class)
    public ProblemDetail handleNotFound(ProductNotFoundException ex) {
        return problem(HttpStatus.NOT_FOUND, "Product not found", ex.getMessage(), "product-not-found");
    }

    @ExceptionHandler(UpstreamException.class)
    public ProblemDetail handleUpstream(UpstreamException ex) {
        log.error("Upstream failure: {}", ex.getMessage());
        return problem(HttpStatus.BAD_GATEWAY, "Upstream source error",
                "The product source is currently unavailable.", "upstream-error");
    }

    /** Simple bad requests whose own message is already client-safe. */
    @ExceptionHandler({
            InvalidRequestException.class,
            MissingServletRequestParameterException.class,
            MethodArgumentTypeMismatchException.class
    })
    public ProblemDetail handleBadRequest(Exception ex) {
        return problem(HttpStatus.BAD_REQUEST, "Invalid request", ex.getMessage(), "bad-request");
    }

    /** Bean Validation on a request body ({@code @Valid} DTO): report each field error. */
    @ExceptionHandler(MethodArgumentNotValidException.class)
    public ProblemDetail handleBodyValidation(MethodArgumentNotValidException ex) {
        String detail = ex.getBindingResult().getFieldErrors().stream()
                .map(fe -> fe.getField() + ": " + fe.getDefaultMessage())
                .collect(Collectors.joining("; "));
        return validationProblem(detail);
    }

    /** Bean Validation on controller method parameters ({@code @RequestParam}/{@code @PathVariable}). */
    @ExceptionHandler(HandlerMethodValidationException.class)
    public ProblemDetail handleParameterValidation(HandlerMethodValidationException ex) {
        String detail = ex.getAllErrors().stream()
                .map(MessageSourceResolvable::getDefaultMessage)
                .collect(Collectors.joining("; "));
        return validationProblem(detail);
    }

    /** Bean Validation surfaced as constraint violations (defensive; also covers direct usages). */
    @ExceptionHandler(ConstraintViolationException.class)
    public ProblemDetail handleConstraintViolation(ConstraintViolationException ex) {
        String detail = ex.getConstraintViolations().stream()
                .map(v -> v.getPropertyPath() + ": " + v.getMessage())
                .collect(Collectors.joining("; "));
        return validationProblem(detail);
    }

    @ExceptionHandler(AuthenticationException.class)
    public ProblemDetail handleAuthentication(AuthenticationException ex) {
        // Log the real reason (never the submitted credentials), but return a generic detail: the
        // specific cause (bad password vs. locked/disabled/expired account) must not leak to the
        // client, as it enables account enumeration and state probing.
        log.warn("Authentication failed: {}", ex.getMessage());
        return problem(HttpStatus.UNAUTHORIZED, "Authentication failed",
                "Invalid username or password.", "unauthorized");
    }

    @ExceptionHandler(AccessDeniedException.class)
    public ProblemDetail handleAccessDenied(AccessDeniedException ex) {
        log.warn("Access denied: {}", ex.getMessage());
        return problem(HttpStatus.FORBIDDEN, "Access denied", ex.getMessage(), "forbidden");
    }

    @ExceptionHandler(Exception.class)
    public ProblemDetail handleUnexpected(Exception ex) {
        log.error("Unexpected error", ex);
        return problem(HttpStatus.INTERNAL_SERVER_ERROR, "Internal server error",
                "An unexpected error occurred.", "internal-error");
    }

    private ProblemDetail validationProblem(String detail) {
        return problem(HttpStatus.BAD_REQUEST, "Validation failed",
                detail.isBlank() ? "Invalid request" : detail, "validation-failed");
    }

    private ProblemDetail problem(HttpStatus status, String title, String detail, String type) {
        ProblemDetail pd = ProblemDetail.forStatusAndDetail(status, detail);
        pd.setTitle(title);
        pd.setType(URI.create("https://abysalto.middleware/errors/" + type));
        pd.setProperty("timestamp", Instant.now().toString());
        return pd;
    }
}
