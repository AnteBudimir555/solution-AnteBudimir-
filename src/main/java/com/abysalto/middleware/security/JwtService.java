package com.abysalto.middleware.security;

import com.abysalto.middleware.config.JwtProperties;
import io.jsonwebtoken.Claims;
import io.jsonwebtoken.JwtException;
import io.jsonwebtoken.Jwts;
import io.jsonwebtoken.security.Keys;
import org.springframework.stereotype.Service;

import javax.crypto.SecretKey;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.time.Instant;
import java.util.Date;
import java.util.Optional;

/**
 * Issues and validates HS256 JWTs for locally-authenticated users. Tokens carry the username as
 * the subject and are signed with the configured secret; validation also enforces the issuer.
 * The algorithm is pinned explicitly so it never changes implicitly with the key length.
 */
@Service
public class JwtService {

    private final SecretKey key;
    private final String issuer;
    private final Duration expiration;

    public JwtService(JwtProperties props) {
        this.key = Keys.hmacShaKeyFor(props.secret().getBytes(StandardCharsets.UTF_8));
        this.issuer = props.issuer();
        this.expiration = Duration.ofMinutes(props.expirationMinutes());
    }

    /** Signs a token for the given username. */
    public String issueToken(String username) {
        Instant now = Instant.now();
        return Jwts.builder()
                .subject(username)
                .issuer(issuer)
                .issuedAt(Date.from(now))
                .expiration(Date.from(now.plus(expiration)))
                .signWith(key, Jwts.SIG.HS256)
                .compact();
    }

    /** Seconds until an issued token expires (for the login response body). */
    public long expiresInSeconds() {
        return expiration.toSeconds();
    }

    /**
     * Returns the subject (username) if the token is a valid, unexpired token signed by us, or an
     * empty {@link Optional} otherwise. Never throws on malformed input.
     */
    public Optional<String> extractUsername(String token) {
        try {
            Claims claims = Jwts.parser()
                    .verifyWith(key)
                    .requireIssuer(issuer)
                    .build()
                    .parseSignedClaims(token)
                    .getPayload();
            return Optional.ofNullable(claims.getSubject());
        } catch (JwtException | IllegalArgumentException ex) {
            return Optional.empty();
        }
    }
}
