package com.abysalto.middleware.security;

import com.abysalto.middleware.config.JwtProperties;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.NullSource;
import org.junit.jupiter.params.provider.ValueSource;

import static org.assertj.core.api.Assertions.assertThat;

/**
 * Unit tests for {@link JwtService}: a valid round-trip returns the subject, and every failure mode
 * (expired, wrong secret, wrong issuer, tampered, malformed) yields an empty result rather than
 * throwing — the filter relies on that never-throw contract.
 */
class JwtServiceTest {

    private static final String SECRET = "unit-test-secret-key-that-is-quite-long-enough-for-hs256-0123456789";
    private static final String ISSUER = "abysalto-middleware";

    private JwtService serviceWith(String secret, String issuer, long expirationMinutes) {
        return new JwtService(new JwtProperties(secret, expirationMinutes, issuer));
    }

    private JwtService service() {
        return serviceWith(SECRET, ISSUER, 60);
    }

    @Test
    void issuesAndValidatesTokenRoundTrip() {
        JwtService service = service();
        String token = service.issueToken("demo");
        assertThat(service.extractUsername(token)).contains("demo");
    }

    @Test
    void reportsExpiryInSeconds() {
        assertThat(serviceWith(SECRET, ISSUER, 60).expiresInSeconds()).isEqualTo(3600);
    }

    @Test
    void rejectsExpiredToken() {
        // Negative expiry issues an already-expired token.
        JwtService issuer = serviceWith(SECRET, ISSUER, -1);
        String expired = issuer.issueToken("demo");
        assertThat(service().extractUsername(expired)).isEmpty();
    }

    @Test
    void rejectsTokenSignedWithDifferentSecret() {
        String token = serviceWith("another-secret-key-of-sufficient-length-for-hs256-abcdefghij", ISSUER, 60)
                .issueToken("demo");
        assertThat(service().extractUsername(token)).isEmpty();
    }

    @Test
    void rejectsTokenWithUnexpectedIssuer() {
        String token = serviceWith(SECRET, "someone-else", 60).issueToken("demo");
        assertThat(service().extractUsername(token)).isEmpty();
    }

    @Test
    void rejectsTamperedToken() {
        String token = service().issueToken("demo");
        // Flip the final character of the signature.
        char last = token.charAt(token.length() - 1);
        String tampered = token.substring(0, token.length() - 1) + (last == 'a' ? 'b' : 'a');
        assertThat(service().extractUsername(tampered)).isEmpty();
    }

    @ParameterizedTest
    @NullSource
    @ValueSource(strings = {"", "   ", "not-a-jwt", "a.b.c", "header.payload"})
    void rejectsMalformedTokenWithoutThrowing(String malformed) {
        assertThat(service().extractUsername(malformed)).isEmpty();
    }
}
