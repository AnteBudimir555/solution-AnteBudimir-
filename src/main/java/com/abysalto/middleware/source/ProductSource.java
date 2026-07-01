package com.abysalto.middleware.source;

import com.abysalto.middleware.domain.Product;
import com.abysalto.middleware.domain.ProductPage;

import java.util.List;

/**
 * Abstraction over a product source. This is the central extension point of the middleware:
 * the current implementation talks to the DummyJSON REST API, but a database, file system or
 * RSS-backed source can be added later simply by providing another implementation — no consumer
 * (service/controller) needs to change.
 *
 * <p>Methods expose only the capabilities a source is expected to support natively. Cross-cutting
 * concerns that not every source can push down (e.g. price-range filtering) are handled in the
 * service layer on top of these primitives.
 *
 * <p>Pagination convention: {@code skip} is a zero-based offset and {@code limit} is the page size;
 * a {@code limit} of {@code 0} requests <em>all</em> matching items (used by the service when it
 * needs the full result set to apply in-service filtering before paginating).
 */
public interface ProductSource {

    /** Returns a page of all products. */
    ProductPage list(int skip, int limit);

    /**
     * Returns the full detail of a single product.
     *
     * @throws com.abysalto.middleware.exception.ProductNotFoundException if no product has that id
     */
    Product getById(long id);

    /** Returns a page of products belonging to the given category. */
    ProductPage findByCategory(String category, int skip, int limit);

    /** Returns a page of products whose name/title matches the free-text query. */
    ProductPage searchByName(String query, int skip, int limit);

    /** Returns the list of available category identifiers. */
    List<String> categories();
}
