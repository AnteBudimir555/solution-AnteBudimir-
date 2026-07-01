package com.abysalto.middleware.dto;

import io.swagger.v3.oas.annotations.media.Schema;

/**
 * Successful login response carrying the signed JWT and how to use it.
 */
public record LoginResponse(
        @Schema(description = "Signed JWT to send as 'Authorization: Bearer <token>'")
        String token,

        @Schema(description = "Token scheme to use in the Authorization header", example = "Bearer")
        String tokenType,

        @Schema(description = "Seconds until the token expires", example = "3600")
        long expiresInSeconds
) {
    public static LoginResponse bearer(String token, long expiresInSeconds) {
        return new LoginResponse(token, "Bearer", expiresInSeconds);
    }
}
