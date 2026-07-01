package com.abysalto.middleware.config;

import io.swagger.v3.oas.models.Components;
import io.swagger.v3.oas.models.OpenAPI;
import io.swagger.v3.oas.models.info.Info;
import io.swagger.v3.oas.models.info.License;
import io.swagger.v3.oas.models.security.SecurityRequirement;
import io.swagger.v3.oas.models.security.SecurityScheme;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;

/**
 * Central OpenAPI/Swagger configuration: API metadata plus a JWT bearer scheme that is declared
 * once and enforced globally in the documentation.
 *
 * <h2>Global security</h2>
 * The {@code bearerAuth} scheme is both registered under {@code components} and attached as a
 * top-level {@link SecurityRequirement}. Because the requirement is global, <em>every</em> operation
 * is documented as needing a bearer token automatically — controllers no longer carry a per-class or
 * per-method {@code @SecurityRequirement}, so the docs cannot silently drift as endpoints are added.
 *
 * <h2>Excluding public endpoints</h2>
 * Endpoints that must stay open (e.g. {@code POST /api/auth/login}) opt out by annotating the handler
 * with an empty
 * {@link io.swagger.v3.oas.annotations.security.SecurityRequirements @SecurityRequirements()}, which
 * clears the inherited requirement for that single operation and hides its Swagger "lock". This is
 * documentation only; actual enforcement lives in {@code SecurityConfig}, and the two must agree.
 *
 * <h2>Metadata</h2>
 * Title, description and version are externalized to {@code openapi.*} configuration (see
 * {@code application.yml}) with sensible in-code defaults, so a release can bump the API version
 * without a recompile. The API version is intentionally independent of the Maven artifact version.
 */
@Configuration
public class OpenApiConfig {

    /** Scheme identifier shared by the component definition and the global requirement. */
    private static final String BEARER_SCHEME = "bearerAuth";

    @Bean
    public OpenAPI middlewareOpenAPI(
            @Value("${openapi.title:Product Middleware API}") String title,
            @Value("${openapi.description:Middleware that re-exposes DummyJSON products with a trimmed "
                    + "shape, filtering, name search, JWT auth and caching.}") String description,
            @Value("${openapi.version:v1}") String version) {

        return new OpenAPI()
                .info(new Info()
                        .title(title)
                        .description(description)
                        .version(version)
                        .license(new License().name("MIT")))
                .components(new Components().addSecuritySchemes(BEARER_SCHEME, jwtBearerScheme()))
                // Applied to every operation; public endpoints opt out via @SecurityRequirements().
                .addSecurityItem(new SecurityRequirement().addList(BEARER_SCHEME));
    }

    /** HTTP bearer scheme that makes Swagger UI send {@code Authorization: Bearer <token>}. */
    private SecurityScheme jwtBearerScheme() {
        return new SecurityScheme()
                .type(SecurityScheme.Type.HTTP)
                .scheme("bearer")
                .bearerFormat("JWT")
                .description("Paste a JWT obtained from POST /api/auth/login");
    }
}
