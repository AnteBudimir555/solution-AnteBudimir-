package com.abysalto.middleware.source.dummyjson;

import com.abysalto.middleware.domain.Product;
import com.abysalto.middleware.domain.ProductPage;
import com.abysalto.middleware.exception.ProductNotFoundException;
import com.abysalto.middleware.exception.UpstreamException;
import com.abysalto.middleware.source.ProductSource;
import com.abysalto.middleware.source.dummyjson.dto.DummyProduct;
import com.abysalto.middleware.source.dummyjson.dto.DummyProductList;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.http.HttpStatusCode;
import org.springframework.http.client.ClientHttpResponse;
import org.springframework.stereotype.Component;
import org.springframework.web.client.RestClient;
import org.springframework.web.client.RestClientResponseException;

import java.io.IOException;
import java.util.List;
import java.util.function.Supplier;

/**
 * {@link ProductSource} backed by the DummyJSON REST API.
 *
 * <p>Filter/search push-down policy (documented in the README):
 * <ul>
 *   <li>Name search and category filtering are pushed down to the upstream
 *       ({@code /products/search}, {@code /products/category/{slug}}).</li>
 *   <li>Pagination uses the upstream {@code limit}/{@code skip} parameters.</li>
 *   <li>Price-range filtering is <em>not</em> supported by DummyJSON, so it is applied
 *       in-service (see {@code ProductService}); this source only exposes the primitives.</li>
 * </ul>
 *
 * <p>Calls are synchronous: the middleware runs on the servlet (Web MVC) stack and uses the blocking
 * {@link RestClient}, so no reactive types leak beyond this class.
 */
@Component
public class DummyJsonProductSource implements ProductSource {

    private static final Logger log = LoggerFactory.getLogger(DummyJsonProductSource.class);

    private final RestClient restClient;

    public DummyJsonProductSource(RestClient dummyJsonRestClient) {
        this.restClient = dummyJsonRestClient;
    }

    @Override
    public ProductPage list(int skip, int limit) {
        log.debug("Upstream list: skip={}, limit={}", skip, limit);
        DummyProductList body = fetch("list products", () -> restClient.get()
                .uri(uri -> uri.path("/products")
                        .queryParam("limit", limit)
                        .queryParam("skip", skip)
                        .build())
                .retrieve()
                .onStatus(HttpStatusCode::isError, (request, response) -> {
                    throw upstreamError(response);
                })
                .body(DummyProductList.class));
        return toPage(body);
    }

    @Override
    public Product getById(long id) {
        log.debug("Upstream getById: id={}", id);
        DummyProduct body = fetch("get product " + id, () -> restClient.get()
                .uri("/products/{id}", id)
                .retrieve()
                .onStatus(status -> status.value() == 404, (request, response) -> {
                    throw new ProductNotFoundException(id);
                })
                .onStatus(HttpStatusCode::isError, (request, response) -> {
                    throw upstreamError(response);
                })
                .body(DummyProduct.class));
        return DummyProductMapper.toDomain(body);
    }

    @Override
    public ProductPage findByCategory(String category, int skip, int limit) {
        log.debug("Upstream findByCategory: category={}, skip={}, limit={}", category, skip, limit);
        DummyProductList body = fetch("filter by category " + category, () -> restClient.get()
                .uri(uri -> uri.path("/products/category/{category}")
                        .queryParam("limit", limit)
                        .queryParam("skip", skip)
                        .build(category))
                .retrieve()
                .onStatus(HttpStatusCode::isError, (request, response) -> {
                    throw upstreamError(response);
                })
                .body(DummyProductList.class));
        return toPage(body);
    }

    @Override
    public ProductPage searchByName(String query, int skip, int limit) {
        log.debug("Upstream searchByName: q={}, skip={}, limit={}", query, skip, limit);
        DummyProductList body = fetch("search products", () -> restClient.get()
                .uri(uri -> uri.path("/products/search")
                        .queryParam("q", query)
                        .queryParam("limit", limit)
                        .queryParam("skip", skip)
                        .build())
                .retrieve()
                .onStatus(HttpStatusCode::isError, (request, response) -> {
                    throw upstreamError(response);
                })
                .body(DummyProductList.class));
        return toPage(body);
    }

    @Override
    public List<String> categories() {
        log.debug("Upstream categories");
        String[] body = fetch("list categories", () -> restClient.get()
                .uri("/products/category-list")
                .retrieve()
                .onStatus(HttpStatusCode::isError, (request, response) -> {
                    throw upstreamError(response);
                })
                .body(String[].class));
        return body == null ? List.of() : List.of(body);
    }

    private ProductPage toPage(DummyProductList body) {
        if (body == null || body.products() == null) {
            return new ProductPage(List.of(), 0, 0, 0);
        }
        List<Product> items = body.products().stream().map(DummyProductMapper::toDomain).toList();
        return new ProductPage(items, body.total(), body.skip(), body.limit());
    }

    /** Maps any error HTTP status into an {@link UpstreamException}. */
    private UpstreamException upstreamError(ClientHttpResponse response) throws IOException {
        return new UpstreamException("DummyJSON returned " + response.getStatusCode());
    }

    /**
     * Executes an upstream call and normalizes failures: {@link ProductNotFoundException} propagates
     * as-is, everything else (timeouts, connection errors, non-2xx) becomes {@link UpstreamException}.
     */
    private <T> T fetch(String description, Supplier<T> call) {
        try {
            return call.get();
        } catch (ProductNotFoundException | UpstreamException e) {
            throw e;
        } catch (RestClientResponseException e) {
            log.error("Upstream error during {}: {}", description, e.getStatusCode());
            throw new UpstreamException("Upstream error during " + description + ": " + e.getStatusCode(), e);
        } catch (Exception e) {
            log.error("Upstream call failed during {}: {}", description, e.getMessage());
            throw new UpstreamException("Upstream call failed during " + description, e);
        }
    }
}
