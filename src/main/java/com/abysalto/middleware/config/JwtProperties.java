package com.abysalto.middleware.config;

import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.Positive;
import jakarta.validation.constraints.Size;
import org.springframework.boot.context.properties.ConfigurationProperties;
import org.springframework.validation.annotation.Validated;

/**
 * JWT signing configuration, bound from {@code middleware.jwt.*}. Invalid configuration fails the
 * application at startup rather than surfacing later as runtime errors. The secret must be at least
 * 32 bytes for HS256; override it via the {@code JWT_SECRET} environment variable in real envs.
 */
@Validated
@ConfigurationProperties(prefix = "middleware.jwt")
public record JwtProperties(
        @NotBlank @Size(min = 32, message = "JWT secret must be at least 32 characters for HS256")
        String secret,

        @Positive
        long expirationMinutes,

        @NotBlank
        String issuer
) {
}
