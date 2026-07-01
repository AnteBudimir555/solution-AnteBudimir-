package com.abysalto.middleware.web;

import jakarta.servlet.FilterChain;
import jakarta.servlet.ServletException;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.slf4j.MDC;
import org.springframework.core.Ordered;
import org.springframework.core.annotation.Order;
import org.springframework.stereotype.Component;
import org.springframework.web.filter.OncePerRequestFilter;

import java.io.IOException;
import java.util.UUID;
import java.util.regex.Pattern;

/**
 * Tags every request with a correlation id and emits a leveled access log line on entry and exit.
 *
 * <p>The correlation id is placed in the SLF4J {@link MDC} under {@value #CORRELATION_ID_MDC_KEY}
 * (referenced by the {@code logging.pattern.console} in {@code application.yml}), so every log line
 * produced on the request thread can be tied back to a single client call. It is also echoed on the
 * response as {@value #CORRELATION_ID_HEADER} so clients and downstream systems can quote it when
 * reporting issues.
 *
 * <p>The request method/path (and, on completion, the response status and duration) are also written
 * to the MDC so the JSON encoder used by the {@code postgres} profile emits them as discrete,
 * queryable fields. The human-readable console pattern only renders the correlation id, so these
 * extra keys are invisible there.
 *
 * <p>Runs at {@link Ordered#HIGHEST_PRECEDENCE} so the id is established before the Spring Security
 * chain executes; authentication/authorization failures rendered from within that chain therefore
 * still carry the correlation id. The MDC is always cleared in a {@code finally} block to avoid
 * leaking the id onto pooled request threads.
 *
 * <p>No request headers or bodies are logged, so credentials (Authorization header, login payload)
 * never reach the logs. An inbound correlation id is only reused when it matches a strict safe
 * pattern, which also prevents log-injection via crafted header values.
 */
@Component
@Order(Ordered.HIGHEST_PRECEDENCE)
public class RequestLoggingFilter extends OncePerRequestFilter {

    /** MDC key holding the correlation id; must match the {@code %X{correlationId}} log pattern. */
    public static final String CORRELATION_ID_MDC_KEY = "correlationId";

    /** MDC keys for the request-scoped fields the JSON encoder emits (unused by the text pattern). */
    private static final String METHOD_MDC_KEY = "method";
    private static final String PATH_MDC_KEY = "path";
    private static final String STATUS_MDC_KEY = "status";
    private static final String DURATION_MDC_KEY = "durationMs";

    /** Request/response header carrying the correlation id. */
    public static final String CORRELATION_ID_HEADER = "X-Correlation-Id";

    /** Accept an inbound id only if it is short and free of control characters (log-injection safe). */
    private static final Pattern SAFE_CORRELATION_ID = Pattern.compile("[A-Za-z0-9._-]{1,64}");

    private static final Logger log = LoggerFactory.getLogger(RequestLoggingFilter.class);

    @Override
    protected void doFilterInternal(HttpServletRequest request,
                                    HttpServletResponse response,
                                    FilterChain filterChain) throws ServletException, IOException {
        String correlationId = resolveCorrelationId(request);
        MDC.put(CORRELATION_ID_MDC_KEY, correlationId);
        MDC.put(METHOD_MDC_KEY, request.getMethod());
        MDC.put(PATH_MDC_KEY, requestPath(request));
        response.setHeader(CORRELATION_ID_HEADER, correlationId);

        long startNanos = System.nanoTime();
        try {
            filterChain.doFilter(request, response);
        } finally {
            // Single completion line (also emitted on failure via finally): carries method, path,
            // status and duration, so a separate entry line would only add noise.
            long tookMs = (System.nanoTime() - startNanos) / 1_000_000;
            MDC.put(STATUS_MDC_KEY, Integer.toString(response.getStatus()));
            MDC.put(DURATION_MDC_KEY, Long.toString(tookMs));
            log.info("{} {} -> {} ({} ms)", request.getMethod(), requestPath(request),
                    response.getStatus(), tookMs);
            // Clear the whole MDC (not just the correlation id) so none of the request-scoped keys
            // leak onto the pooled request thread.
            MDC.clear();
        }
    }

    private String resolveCorrelationId(HttpServletRequest request) {
        String inbound = request.getHeader(CORRELATION_ID_HEADER);
        if (inbound != null && SAFE_CORRELATION_ID.matcher(inbound).matches()) {
            return inbound;
        }
        return UUID.randomUUID().toString();
    }

    private String requestPath(HttpServletRequest request) {
        String query = request.getQueryString();
        return query == null ? request.getRequestURI() : request.getRequestURI() + '?' + query;
    }
}
