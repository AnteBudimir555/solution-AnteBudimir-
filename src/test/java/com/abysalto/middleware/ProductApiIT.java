package com.abysalto.middleware;

import com.abysalto.middleware.domain.Product;
import com.abysalto.middleware.domain.ProductPage;
import com.abysalto.middleware.exception.ProductNotFoundException;
import com.abysalto.middleware.exception.UpstreamException;
import com.abysalto.middleware.model.Role;
import com.abysalto.middleware.model.UserAccount;
import com.abysalto.middleware.repository.UserRepository;
import com.abysalto.middleware.source.ProductSource;
import com.abysalto.middleware.support.TestData;
import com.jayway.jsonpath.JsonPath;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.webmvc.test.autoconfigure.AutoConfigureMockMvc;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.http.HttpHeaders;
import org.springframework.http.MediaType;
import org.springframework.security.crypto.password.PasswordEncoder;
import org.springframework.test.context.ActiveProfiles;
import org.springframework.test.context.bean.override.mockito.MockitoBean;
import org.springframework.test.web.servlet.MockMvc;

import java.math.BigDecimal;
import java.util.List;

import static org.mockito.ArgumentMatchers.anyInt;
import static org.mockito.Mockito.when;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.post;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.content;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

/**
 * End-to-end integration test of the product API: real controller, service, cache, security chain,
 * exception handler and H2-backed user store wired together via {@code @SpringBootTest}. Only the
 * network boundary ({@link ProductSource}) is mocked, so the JWT auth flow, RFC-7807 error contract
 * and Bean Validation are all exercised as a client would hit them.
 */
@SpringBootTest
@AutoConfigureMockMvc
@ActiveProfiles("test")
class ProductApiIT {

    private static final String USERNAME = "tester";
    private static final String PASSWORD = "pass1234";
    private static final String PROBLEM_JSON = "application/problem+json";

    @Autowired
    private MockMvc mvc;

    @Autowired
    private UserRepository users;

    @Autowired
    private PasswordEncoder passwordEncoder;

    @MockitoBean
    private ProductSource source;

    @BeforeEach
    void seedUser() {
        users.deleteAll();
        users.save(new UserAccount(USERNAME, passwordEncoder.encode(PASSWORD), Role.USER));
    }

    private String login(String username, String password) throws Exception {
        String body = """
                {"username":"%s","password":"%s"}
                """.formatted(username, password);
        String response = mvc.perform(post("/api/auth/login")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content(body))
                .andExpect(status().isOk())
                .andReturn().getResponse().getContentAsString();
        return JsonPath.read(response, "$.token");
    }

    private String bearer() throws Exception {
        return "Bearer " + login(USERNAME, PASSWORD);
    }

    // --- authentication ----------------------------------------------------

    @Test
    void loginWithValidCredentialsReturnsToken() throws Exception {
        mvc.perform(post("/api/auth/login")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"username\":\"" + USERNAME + "\",\"password\":\"" + PASSWORD + "\"}"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.token").isNotEmpty())
                .andExpect(jsonPath("$.tokenType").value("Bearer"))
                .andExpect(jsonPath("$.expiresInSeconds").value(3600));
    }

    @Test
    void loginWithBadPasswordReturns401ProblemDetail() throws Exception {
        mvc.perform(post("/api/auth/login")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"username\":\"" + USERNAME + "\",\"password\":\"wrong\"}"))
                .andExpect(status().isUnauthorized())
                .andExpect(content().contentTypeCompatibleWith(PROBLEM_JSON))
                // Generic detail: the specific cause must not leak (no account enumeration).
                .andExpect(jsonPath("$.detail").value("Invalid username or password."));
    }

    @Test
    void protectedEndpointWithoutTokenReturns401() throws Exception {
        mvc.perform(get("/api/products"))
                .andExpect(status().isUnauthorized())
                .andExpect(content().contentTypeCompatibleWith(PROBLEM_JSON));
    }

    @Test
    void protectedEndpointWithInvalidTokenReturns401() throws Exception {
        mvc.perform(get("/api/products").header(HttpHeaders.AUTHORIZATION, "Bearer not-a-real-token"))
                .andExpect(status().isUnauthorized());
    }

    // --- product endpoints (authenticated) ---------------------------------

    @Test
    void listReturnsPagedSummariesWhenAuthenticated() throws Exception {
        List<Product> items = List.of(
                TestData.product(1, "Phone", "A phone", new BigDecimal("9.99"), "smartphones"),
                TestData.product(2, "Laptop", "A laptop", new BigDecimal("999"), "laptops"));
        when(source.list(0, 20)).thenReturn(new ProductPage(items, 2, 0, 20));

        mvc.perform(get("/api/products").header(HttpHeaders.AUTHORIZATION, bearer()))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.items.length()").value(2))
                .andExpect(jsonPath("$.items[0].name").value("Phone"))
                .andExpect(jsonPath("$.totalItems").value(2));
    }

    @Test
    void getByIdReturnsFullDetail() throws Exception {
        when(source.getById(1)).thenReturn(
                TestData.product(1, "Phone", "A phone", new BigDecimal("9.99"), "smartphones"));

        mvc.perform(get("/api/products/1").header(HttpHeaders.AUTHORIZATION, bearer()))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.id").value(1))
                .andExpect(jsonPath("$.name").value("Phone"))
                .andExpect(jsonPath("$.category").value("smartphones"));
    }

    @Test
    void getByMissingIdReturns404ProblemDetail() throws Exception {
        when(source.getById(999)).thenThrow(new ProductNotFoundException(999));

        mvc.perform(get("/api/products/999").header(HttpHeaders.AUTHORIZATION, bearer()))
                .andExpect(status().isNotFound())
                .andExpect(content().contentTypeCompatibleWith(PROBLEM_JSON))
                .andExpect(jsonPath("$.type").value("https://abysalto.middleware/errors/product-not-found"))
                .andExpect(jsonPath("$.timestamp").isNotEmpty());
    }

    @Test
    void getByNonPositiveIdReturns400() throws Exception {
        mvc.perform(get("/api/products/-1").header(HttpHeaders.AUTHORIZATION, bearer()))
                .andExpect(status().isBadRequest())
                .andExpect(content().contentTypeCompatibleWith(PROBLEM_JSON));
    }

    @Test
    void filterWithMinGreaterThanMaxReturns400() throws Exception {
        mvc.perform(get("/api/products/filter")
                        .param("minPrice", "100")
                        .param("maxPrice", "10")
                        .header(HttpHeaders.AUTHORIZATION, bearer()))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.detail").value("minPrice must not be greater than maxPrice"));
    }

    @Test
    void searchWithoutQueryReturns400() throws Exception {
        mvc.perform(get("/api/products/search").header(HttpHeaders.AUTHORIZATION, bearer()))
                .andExpect(status().isBadRequest())
                .andExpect(content().contentTypeCompatibleWith(PROBLEM_JSON));
    }

    @Test
    void upstreamFailureIsReportedAs502() throws Exception {
        when(source.list(anyInt(), anyInt())).thenThrow(new UpstreamException("boom"));

        mvc.perform(get("/api/products").header(HttpHeaders.AUTHORIZATION, bearer()))
                .andExpect(status().isBadGateway())
                .andExpect(content().contentTypeCompatibleWith(PROBLEM_JSON))
                .andExpect(jsonPath("$.type").value("https://abysalto.middleware/errors/upstream-error"));
    }
}
