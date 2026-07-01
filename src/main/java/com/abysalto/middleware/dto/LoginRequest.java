package com.abysalto.middleware.dto;

import io.swagger.v3.oas.annotations.media.Schema;
import jakarta.validation.constraints.NotBlank;

/**
 * Credentials submitted to {@code POST /api/auth/login}.
 */
public record LoginRequest(
        @Schema(description = "Account username", example = "demo")
        @NotBlank String username,

        @Schema(description = "Account password", example = "demo1234")
        @NotBlank String password
) {
}
