package com.abysalto.middleware.source.dummyjson;

import com.abysalto.middleware.domain.Product;
import com.abysalto.middleware.domain.ProductPage;
import com.abysalto.middleware.exception.ProductNotFoundException;
import com.abysalto.middleware.exception.UpstreamException;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.http.HttpStatus;
import org.springframework.http.MediaType;
import org.springframework.test.web.client.MockRestServiceServer;
import org.springframework.web.client.RestClient;

import java.math.BigDecimal;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;
import static org.hamcrest.Matchers.containsString;
import static org.springframework.test.web.client.match.MockRestRequestMatchers.method;
import static org.springframework.test.web.client.match.MockRestRequestMatchers.requestTo;
import static org.springframework.test.web.client.response.MockRestResponseCreators.withStatus;
import static org.springframework.test.web.client.response.MockRestResponseCreators.withSuccess;
import static org.springframework.http.HttpMethod.GET;

/**
 * Component tests for {@link DummyJsonProductSource} against a mocked upstream. A
 * {@link MockRestServiceServer} is bound directly to the {@link RestClient.Builder}, so the DummyJSON
 * DTO deserialization, the domain mapping and the error-translation policy are all exercised without
 * any real network I/O.
 */
class DummyJsonProductSourceTest {

    private static final String BASE_URL = "https://dummyjson.test";

    private MockRestServiceServer server;
    private DummyJsonProductSource source;

    @BeforeEach
    void setUp() {
        RestClient.Builder builder = RestClient.builder().baseUrl(BASE_URL);
        server = MockRestServiceServer.bindTo(builder).build();
        source = new DummyJsonProductSource(builder.build());
    }

    private static String productJson(long id, String title, double price) {
        return """
                {
                  "id": %d,
                  "title": "%s",
                  "description": "A description",
                  "category": "smartphones",
                  "price": %s,
                  "rating": 4.5,
                  "stock": 10,
                  "reviews": [
                    {"rating":5,"comment":"Great","date":"2024-01-01","reviewerName":"Alice","reviewerEmail":"alice@example.com"}
                  ],
                  "dimensions": {"width":1.0,"height":2.0,"depth":3.0},
                  "meta": {"createdAt":"c","updatedAt":"u","barcode":"b","qrCode":"q"}
                }
                """.formatted(id, title, price);
    }

    private static String listJson(String... products) {
        return """
                {"products":[%s],"total":%d,"skip":0,"limit":30}
                """.formatted(String.join(",", products), products.length);
    }

    @Test
    void listMapsProductsAndPaginationMetadata() {
        server.expect(requestTo(containsString("/products?limit=30&skip=0")))
                .andExpect(method(GET))
                .andRespond(withSuccess(listJson(productJson(1, "Phone", 9.99)), MediaType.APPLICATION_JSON));

        ProductPage page = source.list(0, 30);

        assertThat(page.items()).hasSize(1);
        assertThat(page.total()).isEqualTo(1);
        assertThat(page.items().get(0).title()).isEqualTo("Phone");
        server.verify();
    }

    @Test
    void listReturnsEmptyPageWhenProductsFieldAbsent() {
        server.expect(requestTo(containsString("/products")))
                .andRespond(withSuccess("{\"total\":0,\"skip\":0,\"limit\":30}", MediaType.APPLICATION_JSON));

        ProductPage page = source.list(0, 30);

        assertThat(page.items()).isEmpty();
        assertThat(page.total()).isZero();
    }

    @Test
    void getByIdMapsPriceToBigDecimalAndDropsReviewerEmail() {
        server.expect(requestTo(containsString("/products/1")))
                .andExpect(method(GET))
                .andRespond(withSuccess(productJson(1, "Phone", 9.99), MediaType.APPLICATION_JSON));

        Product product = source.getById(1);

        assertThat(product.price()).isEqualByComparingTo(new BigDecimal("9.99"));
        assertThat(product.reviews()).hasSize(1);
        // The domain Review type has no email field: upstream PII is structurally dropped.
        assertThat(product.reviews().get(0).reviewerName()).isEqualTo("Alice");
    }

    @Test
    void getByIdTranslates404ToProductNotFound() {
        server.expect(requestTo(containsString("/products/999")))
                .andRespond(withStatus(HttpStatus.NOT_FOUND));

        assertThatThrownBy(() -> source.getById(999))
                .isInstanceOf(ProductNotFoundException.class);
    }

    @Test
    void getByIdTranslatesServerErrorToUpstreamException() {
        server.expect(requestTo(containsString("/products/1")))
                .andRespond(withStatus(HttpStatus.INTERNAL_SERVER_ERROR));

        assertThatThrownBy(() -> source.getById(1))
                .isInstanceOf(UpstreamException.class);
    }

    @Test
    void findByCategoryQueriesCategoryPathAndMaps() {
        server.expect(requestTo(containsString("/products/category/smartphones")))
                .andExpect(method(GET))
                .andRespond(withSuccess(listJson(productJson(1, "Phone", 9.99)), MediaType.APPLICATION_JSON));

        ProductPage page = source.findByCategory("smartphones", 0, 30);

        assertThat(page.items()).extracting(Product::title).containsExactly("Phone");
    }

    @Test
    void searchByNameQueriesSearchPathAndMaps() {
        server.expect(requestTo(containsString("/products/search?q=phone")))
                .andExpect(method(GET))
                .andRespond(withSuccess(listJson(productJson(1, "Phone", 9.99)), MediaType.APPLICATION_JSON));

        ProductPage page = source.searchByName("phone", 0, 30);

        assertThat(page.items()).hasSize(1);
    }

    @Test
    void categoriesReturnsListFromUpstream() {
        server.expect(requestTo(containsString("/products/category-list")))
                .andRespond(withSuccess("[\"beauty\",\"laptops\"]", MediaType.APPLICATION_JSON));

        assertThat(source.categories()).containsExactly("beauty", "laptops");
    }

    @Test
    void listTranslatesUpstreamErrorToUpstreamException() {
        server.expect(requestTo(containsString("/products")))
                .andRespond(withStatus(HttpStatus.SERVICE_UNAVAILABLE));

        assertThatThrownBy(() -> source.list(0, 30))
                .isInstanceOf(UpstreamException.class);
    }
}
