package com.abysalto.middleware.controller;

import com.abysalto.middleware.dto.LoginRequest;
import com.abysalto.middleware.dto.LoginResponse;
import com.abysalto.middleware.security.JwtService;
import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;
import jakarta.validation.Valid;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.security.authentication.AuthenticationManager;
import org.springframework.security.authentication.UsernamePasswordAuthenticationToken;
import org.springframework.security.core.Authentication;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

/**
 * Authentication endpoint. Exchanges valid credentials for a signed JWT; bad credentials surface
 * as a 401 via the global exception handler.
 */
@RestController
@RequestMapping("/api/auth")
@Tag(name = "Authentication", description = "Obtain a JWT for accessing protected endpoints")
public class AuthController {

    private static final Logger log = LoggerFactory.getLogger(AuthController.class);

    private final AuthenticationManager authenticationManager;
    private final JwtService jwtService;

    public AuthController(AuthenticationManager authenticationManager, JwtService jwtService) {
        this.authenticationManager = authenticationManager;
        this.jwtService = jwtService;
    }

    @PostMapping("/login")
    @Operation(summary = "Log in", description = "Authenticate with username/password and receive a JWT")
    public LoginResponse login(@Valid @RequestBody LoginRequest request) {
        Authentication authentication = authenticationManager.authenticate(
                new UsernamePasswordAuthenticationToken(request.username(), request.password()));
        String token = jwtService.issueToken(authentication.getName());
        // Log the identity only — never the password or the issued token.
        log.info("Issued JWT for user '{}'", authentication.getName());
        return LoginResponse.bearer(token, jwtService.expiresInSeconds());
    }
}
