package com.abysalto.middleware.source.dummyjson;

import com.abysalto.middleware.domain.Dimensions;
import com.abysalto.middleware.domain.Meta;
import com.abysalto.middleware.domain.Product;
import com.abysalto.middleware.domain.Review;
import com.abysalto.middleware.source.dummyjson.dto.DummyDimensions;
import com.abysalto.middleware.source.dummyjson.dto.DummyMeta;
import com.abysalto.middleware.source.dummyjson.dto.DummyProduct;
import com.abysalto.middleware.source.dummyjson.dto.DummyReview;

import java.math.BigDecimal;
import java.util.List;

/**
 * Maps raw DummyJSON DTOs into the internal {@link Product} domain model. This is the only place
 * that knows the DummyJSON shape; everything downstream works with {@link Product}.
 */
final class DummyProductMapper {

    private DummyProductMapper() {
    }

    static Product toDomain(DummyProduct d) {
        return new Product(
                d.id(),
                d.title(),
                d.description(),
                d.category(),
                d.price() == null ? null : BigDecimal.valueOf(d.price()),
                d.discountPercentage(),
                d.rating(),
                d.stock(),
                d.tags(),
                d.brand(),
                d.sku(),
                d.weight(),
                toDomain(d.dimensions()),
                d.warrantyInformation(),
                d.shippingInformation(),
                d.availabilityStatus(),
                d.returnPolicy(),
                d.minimumOrderQuantity(),
                d.thumbnail(),
                d.images(),
                toReviews(d.reviews()),
                toDomain(d.meta())
        );
    }

    private static Dimensions toDomain(DummyDimensions d) {
        return d == null ? null : new Dimensions(d.width(), d.height(), d.depth());
    }

    private static Meta toDomain(DummyMeta m) {
        return m == null ? null : new Meta(m.createdAt(), m.updatedAt(), m.barcode(), m.qrCode());
    }

    private static List<Review> toReviews(List<DummyReview> reviews) {
        // reviewerEmail is deliberately dropped here: it is upstream PII and is never surfaced by the API.
        return reviews == null ? null : reviews.stream()
                .map(r -> new Review(r.rating(), r.comment(), r.date(), r.reviewerName()))
                .toList();
    }
}
