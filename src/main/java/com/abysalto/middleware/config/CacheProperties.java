package com.abysalto.middleware.config;

import org.springframework.boot.context.properties.ConfigurationProperties;

/**
 * Caffeine cache configuration, bound from {@code middleware.cache.*}.
 *
 * @param spec a Caffeine spec string (e.g. {@code maximumSize=500,expireAfterWrite=60s}) that
 *             bounds the size and sets the write TTL of the search/filter caches
 */
@ConfigurationProperties(prefix = "middleware.cache")
public record CacheProperties(String spec) {
}
