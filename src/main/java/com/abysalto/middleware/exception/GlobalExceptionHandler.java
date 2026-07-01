package com.abysalto.middleware.exception;

import jakarta.validation.ConstraintViolationException;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.context.MessageSourceResolvable;
import org.springframework.http.HttpHeaders;
import org.springframework.http.HttpStatus;
import org.springframework.http.HttpStatusCode;
import org.springframework.http.ProblemDetail;
import org.springframework.http.ResponseEntity;
import org.springframework.security.access.AccessDeniedException;
import org.springframework.security.core.AuthenticationException;
import org.springframework.web.bind.MethodArgumentNotValidException;
import org.springframework.web.bind.annotation.ExceptionHandler;
import org.springframework.web.bind.annotation.RestControllerAdvice;
import org.springframework.web.context.request.WebRequest;
import org.springframework.web.method.annotation.HandlerMethodValidationException;
import org.springframework.web.method.annotation.MethodArgumentTypeMismatchException;
import org.springframework.web.servlet.mvc.method.annotation.ResponseEntityExceptionHandler;

import java.net.URI;
import java.time.Instant;
import java.util.Locale;
import java.util.stream.Collectors;

/**
 * Centralized error handling. Every failure is rendered as an RFC-7807 {@link ProblemDetail} with
 * a sensible HTTP status, so clients get a consistent error body across the API.
 *
 * <p>Extends {@link ResponseEntityExceptionHandler} so the standard Spring MVC exceptions (unknown
 * route, unsupported method/media type, missing parameter, unreadable body, …) keep their correct
 * 4xx status instead of being swallowed by the {@link Exception} fallback and reported as 500. The
 * base class produces {@code ProblemDetail} bodies; {@link #handleExceptionInternal} aligns them with
 * this API's {@code type}/{@code timestamp} convention.
 */
@RestControllerAdvice
public class GlobalExceptionHandler extends ResponseEntityExceptionHandler {

    private static final Logger log = LoggerFactory.getLogger(GlobalExceptionHandler.class);

    private static final String TYPE_PREFIX = "https://abysalto.middleware/errors/";

    // --- domain exceptions -------------------------------------------------

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

    /** Semantically invalid request whose own message is already client-safe (e.g. minPrice > maxPrice). */
    @ExceptionHandler(InvalidRequestException.class)
    public ProblemDetail handleInvalidRequest(InvalidRequestException ex) {
        return problem(HttpStatus.BAD_REQUEST, "Invalid request", ex.getMessage(), "bad-request");
    }

    /**
     * A request parameter/path variable could not be converted to the target type. The raw framework
     * message names internal Java types, so a safe message is built from the parameter name only.
     */
    @ExceptionHandler(MethodArgumentTypeMismatchException.class)
    public ProblemDetail handleTypeMismatch(MethodArgumentTypeMismatchException ex) {
        return problem(HttpStatus.BAD_REQUEST, "Invalid request",
                "Parameter '" + ex.getName() + "' has an invalid value.", "bad-request");
    }

    /** Bean Validation surfaced as constraint violations (defensive; also covers service-layer usage). */
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

    // --- standard MVC validation (field-level detail preserved) ------------

    /** Bean Validation on a request body ({@code @Valid} DTO): report each field error. */
    @Override
    protected ResponseEntity<Object> handleMethodArgumentNotValid(
            MethodArgumentNotValidException ex, HttpHeaders headers, HttpStatusCode status, WebRequest request) {
        String detail = ex.getBindingResult().getFieldErrors().stream()
                .map(fe -> fe.getField() + ": " + fe.getDefaultMessage())
                .collect(Collectors.joining("; "));
        return new ResponseEntity<>(validationProblem(detail), headers, HttpStatus.BAD_REQUEST);
    }

    /** Bean Validation on controller method parameters ({@code @RequestParam}/{@code @PathVariable}). */
    @Override
    protected ResponseEntity<Object> handleHandlerMethodValidationException(
            HandlerMethodValidationException ex, HttpHeaders headers, HttpStatusCode status, WebRequest request) {
        String detail = ex.getAllErrors().stream()
                .map(MessageSourceResolvable::getDefaultMessage)
                .collect(Collectors.joining("; "));
        return new ResponseEntity<>(validationProblem(detail), headers, HttpStatus.BAD_REQUEST);
    }

    /**
     * All other standard MVC exceptions (unknown route, unsupported method/media type, missing
     * parameter, unreadable body, …) are handled by the base class with their correct status; this
     * hook aligns the resulting {@link ProblemDetail} with the API's {@code type}/{@code timestamp}
     * convention.
     */
    @Override
    protected ResponseEntity<Object> handleExceptionInternal(
            Exception ex, Object body, HttpHeaders headers, HttpStatusCode statusCode, WebRequest request) {
        ResponseEntity<Object> response = super.handleExceptionInternal(ex, body, headers, statusCode, request);
        if (response != null && response.getBody() instanceof ProblemDetail pd) {
            enrich(pd);
        }
        return response;
    }

    // --- helpers -----------------------------------------------------------

    private ProblemDetail validationProblem(String detail) {
        return problem(HttpStatus.BAD_REQUEST, "Validation failed",
                detail.isBlank() ? "Invalid request" : detail, "validation-failed");
    }

    private ProblemDetail problem(HttpStatus status, String title, String detail, String type) {
        ProblemDetail pd = ProblemDetail.forStatusAndDetail(status, detail);
        pd.setTitle(title);
        pd.setType(URI.create(TYPE_PREFIX + type));
        pd.setProperty("timestamp", Instant.now().toString());
        return pd;
    }

    /** Applies the API's {@code type}/{@code timestamp} convention to a base-class-produced ProblemDetail. */
    private void enrich(ProblemDetail pd) {
        if (pd.getType() == null || "about:blank".equals(pd.getType().toString())) {
            pd.setType(URI.create(TYPE_PREFIX + slug(pd.getStatus())));
        }
        pd.setProperty("timestamp", Instant.now().toString());
    }

    private static String slug(int status) {
        HttpStatus resolved = HttpStatus.resolve(status);
        return resolved == null ? "error" : resolved.name().toLowerCase(Locale.ROOT).replace('_', '-');
    }
}
