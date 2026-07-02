package com.abysalto.middleware.config;

import io.swagger.v3.core.converter.ModelConverters;
import io.swagger.v3.oas.models.Components;
import io.swagger.v3.oas.models.OpenAPI;
import io.swagger.v3.oas.models.info.Info;
import io.swagger.v3.oas.models.info.License;
import io.swagger.v3.oas.models.media.Content;
import io.swagger.v3.oas.models.media.MediaType;
import io.swagger.v3.oas.models.media.Schema;
import io.swagger.v3.oas.models.responses.ApiResponse;
import io.swagger.v3.oas.models.responses.ApiResponses;
import io.swagger.v3.oas.models.security.SecurityRequirement;
import io.swagger.v3.oas.models.security.SecurityScheme;
import org.springdoc.core.customizers.GlobalOpenApiCustomizer;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.http.ProblemDetail;

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

    /** RFC-7807 media type produced by {@code GlobalExceptionHandler} for every error. */
    private static final String PROBLEM_JSON = "application/problem+json";

    /** Reusable schema name/ref for the {@link ProblemDetail} error body. */
    private static final String PROBLEM_SCHEMA = "ProblemDetail";
    private static final String PROBLEM_SCHEMA_REF = "#/components/schemas/" + PROBLEM_SCHEMA;

    private static final String PRODUCTS_PATH_PREFIX = "/api/products";

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

    /**
     * Documents the common RFC-7807 error responses on every operation, centrally, so the generated
     * spec matches {@code GlobalExceptionHandler} without per-controller annotations:
     * <ul>
     *   <li>{@code 400/401/500} on every operation (validation, missing/invalid token, unexpected error);</li>
     *   <li>{@code 502} on the product endpoints, which call the upstream source;</li>
     *   <li>{@code 404} on operations with a path variable (e.g. {@code /api/products/{id}}).</li>
     * </ul>
     * Existing (annotation-defined) responses are never overwritten.
     */
    @Bean
    public GlobalOpenApiCustomizer errorResponseCustomizer() {
        return openApi -> {
            registerProblemDetailSchema(openApi);
            openApi.getPaths().forEach((path, pathItem) -> pathItem.readOperations().forEach(operation -> {
                ApiResponses responses = operation.getResponses();
                addProblemResponse(responses, "400", "Invalid request parameters or body");
                addProblemResponse(responses, "401", "Missing or invalid bearer token");
                addProblemResponse(responses, "500", "Unexpected server error");
                if (path.startsWith(PRODUCTS_PATH_PREFIX)) {
                    addProblemResponse(responses, "502", "Upstream product source error");
                }
                if (path.contains("{")) {
                    addProblemResponse(responses, "404", "Resource not found");
                }
            }));
        };
    }

    /** Registers the {@link ProblemDetail} schema under components (once) so responses can $ref it. */
    private void registerProblemDetailSchema(OpenAPI openApi) {
        if (openApi.getComponents() == null) {
            openApi.setComponents(new Components());
        }
        if (openApi.getComponents().getSchemas() != null
                && openApi.getComponents().getSchemas().containsKey(PROBLEM_SCHEMA)) {
            return;
        }
        ModelConverters.getInstance().read(ProblemDetail.class)
                .forEach(openApi.getComponents()::addSchemas);
    }

    /** Adds a ProblemDetail-bodied error response for {@code code}, unless one is already documented. */
    private void addProblemResponse(ApiResponses responses, String code, String description) {
        if (responses.containsKey(code)) {
            return;
        }
        responses.addApiResponse(code, new ApiResponse()
                .description(description)
                .content(new Content().addMediaType(PROBLEM_JSON,
                        new MediaType().schema(new Schema<>().$ref(PROBLEM_SCHEMA_REF)))));
    }
}
