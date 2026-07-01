package com.abysalto.middleware.config;

import com.abysalto.middleware.security.AppUserDetailsService;
import com.abysalto.middleware.security.JwtAuthenticationFilter;
import com.abysalto.middleware.security.JwtService;
import org.springframework.beans.factory.annotation.Qualifier;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.security.authentication.AuthenticationManager;
import org.springframework.security.config.annotation.authentication.configuration.AuthenticationConfiguration;
import org.springframework.security.config.annotation.web.builders.HttpSecurity;
import org.springframework.security.config.annotation.web.configurers.AbstractHttpConfigurer;
import org.springframework.security.config.annotation.web.configurers.HeadersConfigurer;
import org.springframework.security.config.http.SessionCreationPolicy;
import org.springframework.security.crypto.bcrypt.BCryptPasswordEncoder;
import org.springframework.security.crypto.password.PasswordEncoder;
import org.springframework.security.web.SecurityFilterChain;
import org.springframework.security.web.authentication.UsernamePasswordAuthenticationFilter;
import org.springframework.web.servlet.HandlerExceptionResolver;

/**
 * Stateless JWT security. Public: auth endpoints, OpenAPI docs and the H2 console. Everything
 * under {@code /api/products/**} (and anything else) requires a valid bearer token.
 * <p>
 * Authentication (401) and access-denied (403) failures occur inside the filter chain, before the
 * MVC layer. Rather than duplicate error rendering, both are delegated to the MVC
 * {@link HandlerExceptionResolver}, so {@code GlobalExceptionHandler} produces the same RFC-7807
 * bodies as the rest of the API.
 */
@Configuration
public class SecurityConfig {

    private static final String[] PUBLIC_PATHS = {
            "/api/auth/**",
            "/v3/api-docs/**",
            "/swagger-ui/**",
            "/swagger-ui.html",
            "/h2-console/**"
    };

    @Bean
    public SecurityFilterChain securityFilterChain(
            HttpSecurity http,
            JwtService jwtService,
            AppUserDetailsService userDetailsService,
            @Qualifier("handlerExceptionResolver") HandlerExceptionResolver resolver) throws Exception {

        // Constructed here (not a @Component) so Boot does not also auto-register it in the main
        // servlet filter chain; it belongs only to this security chain.
        JwtAuthenticationFilter jwtAuthenticationFilter =
                new JwtAuthenticationFilter(jwtService, userDetailsService);

        http
                .csrf(AbstractHttpConfigurer::disable)
                // H2 console renders inside a frame; allow same-origin framing for it.
                .headers(headers -> headers.frameOptions(HeadersConfigurer.FrameOptionsConfig::sameOrigin))
                .sessionManagement(session -> session.sessionCreationPolicy(SessionCreationPolicy.STATELESS))
                .authorizeHttpRequests(auth -> auth
                        .requestMatchers(PUBLIC_PATHS).permitAll()
                        .anyRequest().authenticated())
                .exceptionHandling(ex -> ex
                        .authenticationEntryPoint((request, response, authException) ->
                                resolver.resolveException(request, response, null, authException))
                        .accessDeniedHandler((request, response, deniedException) ->
                                resolver.resolveException(request, response, null, deniedException)))
                .addFilterBefore(jwtAuthenticationFilter, UsernamePasswordAuthenticationFilter.class);

        return http.build();
    }

    @Bean
    public PasswordEncoder passwordEncoder() {
        return new BCryptPasswordEncoder();
    }

    @Bean
    public AuthenticationManager authenticationManager(AuthenticationConfiguration configuration) throws Exception {
        return configuration.getAuthenticationManager();
    }
}
