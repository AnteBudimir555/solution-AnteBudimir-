package com.abysalto.middleware.config;

import org.springframework.boot.context.properties.ConfigurationProperties;

/**
 * Configuration for the DummyJSON upstream source, bound from {@code middleware.upstream.dummyjson.*}.
 */
@ConfigurationProperties(prefix = "middleware.upstream.dummyjson")
public record UpstreamProperties(
        String baseUrl,
        int connectTimeoutMs,
        int responseTimeoutMs
) {
}
