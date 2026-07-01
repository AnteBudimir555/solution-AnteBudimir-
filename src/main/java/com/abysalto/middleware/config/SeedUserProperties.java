package com.abysalto.middleware.config;

import org.springframework.boot.context.properties.ConfigurationProperties;

/**
 * Seed-user configuration, bound from {@code middleware.security.seed-user.*}. When enabled, a
 * single user with these credentials is created on startup so the API is usable out of the box.
 * Disabled under the {@code test} profile (tests create their own fixtures).
 */
@ConfigurationProperties(prefix = "middleware.security.seed-user")
public record SeedUserProperties(
        boolean enabled,
        String username,
        String password
) {
}
